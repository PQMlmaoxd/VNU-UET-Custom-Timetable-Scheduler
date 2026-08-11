#pragma once

#include <chrono>
#include <string>
#include <vector>

namespace scheduler::protocol {

constexpr int kProtocolVersion = 2;

enum class SolveStatus {
  Feasible,
  Infeasible,
  TimedOut,
  InvalidRequest,
  InternalError,
};

struct SolveRequest {
  std::string request_id;
  int variable_count;
  std::vector<std::vector<int>> clauses;
  std::vector<std::vector<int>> exactly_one_groups;
  int max_solutions;
  std::chrono::milliseconds timeout;
};

struct SolveMetrics {
  long long elapsed_milliseconds = 0;
  int solve_calls = 0;
};

struct SolveResponse {
  std::string request_id;
  SolveStatus status;
  std::vector<std::vector<int>> solutions;
  SolveMetrics metrics;
  std::string message;
};

class ProtocolError final : public std::runtime_error {
 public:
  using std::runtime_error::runtime_error;
};

SolveRequest parse_request(const std::string& json);
std::string serialize_response(const SolveResponse& response);
const char* status_name(SolveStatus status);

}  // namespace scheduler::protocol
