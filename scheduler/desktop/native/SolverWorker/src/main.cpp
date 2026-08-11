#include <cadical.hpp>

#include "protocol.hpp"

#include <chrono>
#include <algorithm>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace {

using scheduler::protocol::SolveRequest;
using scheduler::protocol::SolveResponse;
using scheduler::protocol::SolveStatus;

constexpr size_t kMaximumProtocolLineBytes = 64U * 1024U * 1024U;

class DeadlineTerminator final : public CaDiCaL::Terminator {
 public:
  explicit DeadlineTerminator(std::chrono::steady_clock::time_point deadline) : deadline_(deadline) {}

  bool terminate() override {
    return std::chrono::steady_clock::now() >= deadline_;
  }

 private:
  std::chrono::steady_clock::time_point deadline_;
};

int print_version() {
  std::cout << "solver-worker protocol=" << scheduler::protocol::kProtocolVersion
            << " solver=cadical version=" << CaDiCaL::Solver::version() << '\n';
  return 0;
}

std::vector<int> blocking_clause_for(
    const std::vector<int>& model,
    const std::vector<std::vector<int>>& exactly_one_groups);

SolveResponse solve_request(const SolveRequest& request) {
  const auto started_at = std::chrono::steady_clock::now();
  const auto deadline = started_at + request.timeout;
  CaDiCaL::Solver solver;
  if (!solver.set("factor", 0)) {
    throw std::runtime_error("CaDiCaL did not accept the required factor=0 option");
  }

  solver.declare_more_variables(request.variable_count);
  for (const auto& clause : request.clauses) {
    for (const int literal : clause) {
      solver.add(literal);
    }

    solver.add(0);
  }

  DeadlineTerminator terminator(deadline);
  solver.connect_terminator(&terminator);

  std::vector<std::vector<int>> solutions;
  int solve_calls = 0;
  SolveStatus status = SolveStatus::Infeasible;
  while (static_cast<int>(solutions.size()) < request.max_solutions) {
    const int result = solver.solve();
    solve_calls++;
    if (result == CaDiCaL::SATISFIABLE) {
      std::vector<int> model;
      model.reserve(static_cast<size_t>(request.variable_count));
      for (int variable = 1; variable <= request.variable_count; variable++) {
        const int value = solver.val(variable);
        if (value == 0) {
          throw std::runtime_error("CaDiCaL returned an incomplete model");
        }

        model.push_back(value > 0 ? variable : -variable);
      }

      const auto blocking_clause = blocking_clause_for(model, request.exactly_one_groups);
      for (const int literal : blocking_clause) {
        solver.add(literal);
      }
      solver.add(0);
      solutions.push_back(std::move(model));
      status = SolveStatus::Feasible;
      continue;
    }

    if (result == CaDiCaL::UNSATISFIABLE) {
      status = solutions.empty() ? SolveStatus::Infeasible : SolveStatus::Feasible;
      break;
    }

    status = SolveStatus::TimedOut;
    break;
  }

  solver.disconnect_terminator();
  const auto elapsed = std::chrono::steady_clock::now() - started_at;
  return SolveResponse{
      request.request_id,
      status,
      std::move(solutions),
      {std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count(), solve_calls},
      ""};
}

std::vector<int> blocking_clause_for(
    const std::vector<int>& model,
    const std::vector<std::vector<int>>& exactly_one_groups) {
  if (exactly_one_groups.empty()) {
    std::vector<int> blocking_clause;
    blocking_clause.reserve(model.size());
    for (const int literal : model) {
      blocking_clause.push_back(-literal);
    }

    return blocking_clause;
  }

  std::vector<int> blocking_clause;
  blocking_clause.reserve(exactly_one_groups.size());
  for (const auto& group : exactly_one_groups) {
    int selected_variable = 0;
    for (const int variable : group) {
      if (model[static_cast<size_t>(variable - 1)] > 0) {
        if (selected_variable != 0) {
          throw std::runtime_error("exactly_one_groups has multiple selected variables");
        }

        selected_variable = variable;
      }
    }

    if (selected_variable == 0) {
      throw std::runtime_error("exactly_one_groups has no selected variable");
    }

    blocking_clause.push_back(-selected_variable);
  }

  return blocking_clause;
}

int run_protocol(std::istream& input, std::ostream& output) {
  std::string line;
  char character = 0;
  while (input.get(character) && character != '\n') {
    if (line.size() == kMaximumProtocolLineBytes) {
      output << scheduler::protocol::serialize_response(
                    {"", SolveStatus::InvalidRequest, {}, {}, "request exceeds the 64 MiB protocol limit"})
             << '\n';
      return 64;
    }

    line.push_back(character);
  }

  if (line.empty() && !input && input.eof()) {
    output << scheduler::protocol::serialize_response(
                  {"", SolveStatus::InvalidRequest, {}, {}, "a single NDJSON request is required"})
           << '\n';
    return 64;
  }

  while (input.get(character)) {
    if (character != ' ' && character != '\t' && character != '\r' && character != '\n') {
      output << scheduler::protocol::serialize_response(
                    {"", SolveStatus::InvalidRequest, {}, {}, "only one NDJSON request is accepted"})
             << '\n';
      return 64;
    }
  }

  try {
    const SolveRequest request = scheduler::protocol::parse_request(line);
    output << scheduler::protocol::serialize_response(solve_request(request)) << '\n';
    return 0;
  } catch (const scheduler::protocol::ProtocolError& error) {
    output << scheduler::protocol::serialize_response(
                  {"", SolveStatus::InvalidRequest, {}, {}, error.what()})
           << '\n';
    return 64;
  } catch (const std::exception&) {
    output << scheduler::protocol::serialize_response(
                  {"", SolveStatus::InternalError, {}, {}, "solver worker failed unexpectedly"})
           << '\n';
    return 1;
  }
}

int run_protocol_self_test() {
  const SolveRequest request = scheduler::protocol::parse_request(
      R"({"protocol_version":2,"request_id":"native-self-test","variable_count":2,"clauses":[[1,2],[-1,-2]],"exactly_one_groups":[[1,2]],"max_solutions":5,"timeout_milliseconds":1000})");
  const SolveResponse response = solve_request(request);
  if (response.status != SolveStatus::Feasible || response.solutions.size() != 2U ||
      response.metrics.solve_calls != 3) {
    return 1;
  }

  try {
    static_cast<void>(scheduler::protocol::parse_request(
        R"({"protocol_version":2,"request_id":"invalid","variable_count":1,"clauses":[[2]],"exactly_one_groups":[[1]],"max_solutions":1,"timeout_milliseconds":1})"));
    return 1;
  } catch (const scheduler::protocol::ProtocolError&) {
  }

  std::cout << "solver-worker protocol-self-test=ok protocol="
            << scheduler::protocol::kProtocolVersion
            << " solver=cadical version=" << CaDiCaL::Solver::version() << '\n';
  return 0;
}

int run_self_test() {
  // (x1) and (!x1 or x2) has a unique model over the declared variables.
  const SolveRequest request{
       "self-test",
       2,
       {{1}, {-1, 2}},
       {},
       5,
      std::chrono::milliseconds(1000)};
  const SolveResponse response = solve_request(request);

  if (response.status != SolveStatus::Feasible || response.solutions.size() != 1U ||
      response.solutions[0] != std::vector<int>({1, 2})) {
    return 1;
  }

  std::cout << "solver-worker self-test=ok protocol=" << scheduler::protocol::kProtocolVersion
            << " solver=cadical version=" << CaDiCaL::Solver::version() << '\n';
  return 0;
}

}  // namespace

int main(int argc, char* argv[]) {
  if (argc == 2 && std::string_view(argv[1]) == "--version") {
    return print_version();
  }

  if (argc == 2 && std::string_view(argv[1]) == "--self-test") {
    return run_self_test();
  }

  if (argc == 2 && std::string_view(argv[1]) == "--protocol-self-test") {
    return run_protocol_self_test();
  }

  if (argc != 1) {
    std::cerr << "SolverWorker accepts --version, --self-test, --protocol-self-test, or one NDJSON request on stdin.\n";
    return 64;
  }

  return run_protocol(std::cin, std::cout);
}
