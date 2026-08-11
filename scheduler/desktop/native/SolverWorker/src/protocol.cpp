#include "protocol.hpp"

#include <algorithm>
#include <charconv>
#include <cctype>
#include <limits>
#include <optional>
#include <sstream>
#include <string_view>

namespace scheduler::protocol {
namespace {

constexpr size_t kMaximumRequestBytes = 64U * 1024U * 1024U;
constexpr size_t kMaximumResponseBytes = 8U * 1024U * 1024U;
constexpr size_t kMaximumRequestIdLength = 128U;
constexpr size_t kMaximumClauses = 2U * 1000U * 1000U;
constexpr size_t kMaximumLiterals = 10U * 1000U * 1000U;
constexpr int kMaximumVariables = 2 * 1000 * 1000;
constexpr int kMaximumSolutions = 100;
constexpr int kMaximumTimeoutMilliseconds = 300 * 1000;

class JsonReader {
 public:
  explicit JsonReader(std::string_view input) : input_(input) {}

  bool at_end() {
    skip_whitespace();
    return position_ == input_.size();
  }

  void expect(char expected) {
    skip_whitespace();
    if (position_ == input_.size() || input_[position_] != expected) {
      throw ProtocolError(std::string("expected '") + expected + "'");
    }

    position_++;
  }

  bool consume(char expected) {
    skip_whitespace();
    if (position_ == input_.size() || input_[position_] != expected) {
      return false;
    }

    position_++;
    return true;
  }

  std::string parse_string() {
    expect('"');
    std::string value;
    while (position_ < input_.size()) {
      const unsigned char current = static_cast<unsigned char>(input_[position_++]);
      if (current == '"') {
        return value;
      }

      if (current < 0x20U) {
        throw ProtocolError("control character in JSON string");
      }

      if (current != '\\') {
        value.push_back(static_cast<char>(current));
        continue;
      }

      if (position_ == input_.size()) {
        throw ProtocolError("unterminated JSON escape");
      }

      const char escaped = input_[position_++];
      switch (escaped) {
        case '"': value.push_back('"'); break;
        case '\\': value.push_back('\\'); break;
        case '/': value.push_back('/'); break;
        case 'b': value.push_back('\b'); break;
        case 'f': value.push_back('\f'); break;
        case 'n': value.push_back('\n'); break;
        case 'r': value.push_back('\r'); break;
        case 't': value.push_back('\t'); break;
        case 'u': append_unicode_escape(value); break;
        default: throw ProtocolError("invalid JSON escape");
      }
    }

    throw ProtocolError("unterminated JSON string");
  }

  int parse_integer() {
    skip_whitespace();
    const size_t start = position_;
    if (position_ < input_.size() && input_[position_] == '-') {
      position_++;
    }

    if (position_ == input_.size() || !std::isdigit(static_cast<unsigned char>(input_[position_]))) {
      throw ProtocolError("integer expected");
    }

    if (input_[position_] == '0') {
      position_++;
      if (position_ < input_.size() && std::isdigit(static_cast<unsigned char>(input_[position_]))) {
        throw ProtocolError("invalid leading zero in integer");
      }
    } else {
      while (position_ < input_.size() && std::isdigit(static_cast<unsigned char>(input_[position_]))) {
        position_++;
      }
    }

    if (position_ < input_.size() &&
        (input_[position_] == '.' || input_[position_] == 'e' || input_[position_] == 'E')) {
      throw ProtocolError("integer expected");
    }

    int value = 0;
    const char* first = input_.data() + start;
    const char* last = input_.data() + position_;
    const auto [pointer, error] = std::from_chars(first, last, value);
    if (error != std::errc() || pointer != last) {
      throw ProtocolError("integer is outside supported range");
    }

    return value;
  }

  void skip_value() {
    skip_whitespace();
    if (position_ == input_.size()) {
      throw ProtocolError("JSON value expected");
    }

    switch (input_[position_]) {
      case '{': skip_object(); return;
      case '[': skip_array(); return;
      case '"': static_cast<void>(parse_string()); return;
      case 't': consume_literal("true"); return;
      case 'f': consume_literal("false"); return;
      case 'n': consume_literal("null"); return;
      default: skip_number(); return;
    }
  }

 private:
  void skip_whitespace() {
    while (position_ < input_.size() && std::isspace(static_cast<unsigned char>(input_[position_]))) {
      position_++;
    }
  }

  int parse_hex_quad() {
    if (input_.size() - position_ < 4U) {
      throw ProtocolError("incomplete Unicode escape");
    }

    int value = 0;
    for (size_t index = 0; index < 4U; index++) {
      const char character = input_[position_++];
      value <<= 4;
      if (character >= '0' && character <= '9') {
        value += character - '0';
      } else if (character >= 'a' && character <= 'f') {
        value += character - 'a' + 10;
      } else if (character >= 'A' && character <= 'F') {
        value += character - 'A' + 10;
      } else {
        throw ProtocolError("invalid Unicode escape");
      }
    }

    return value;
  }

  void append_unicode_escape(std::string& value) {
    int code_point = parse_hex_quad();
    if (code_point >= 0xD800 && code_point <= 0xDBFF) {
      if (position_ + 2U > input_.size() || input_[position_] != '\\' || input_[position_ + 1U] != 'u') {
        throw ProtocolError("high surrogate without low surrogate");
      }

      position_ += 2U;
      const int low_surrogate = parse_hex_quad();
      if (low_surrogate < 0xDC00 || low_surrogate > 0xDFFF) {
        throw ProtocolError("invalid low surrogate");
      }

      code_point = 0x10000 + ((code_point - 0xD800) << 10) + (low_surrogate - 0xDC00);
    } else if (code_point >= 0xDC00 && code_point <= 0xDFFF) {
      throw ProtocolError("low surrogate without high surrogate");
    }

    if (code_point <= 0x7F) {
      value.push_back(static_cast<char>(code_point));
    } else if (code_point <= 0x7FF) {
      value.push_back(static_cast<char>(0xC0 | (code_point >> 6)));
      value.push_back(static_cast<char>(0x80 | (code_point & 0x3F)));
    } else if (code_point <= 0xFFFF) {
      value.push_back(static_cast<char>(0xE0 | (code_point >> 12)));
      value.push_back(static_cast<char>(0x80 | ((code_point >> 6) & 0x3F)));
      value.push_back(static_cast<char>(0x80 | (code_point & 0x3F)));
    } else {
      value.push_back(static_cast<char>(0xF0 | (code_point >> 18)));
      value.push_back(static_cast<char>(0x80 | ((code_point >> 12) & 0x3F)));
      value.push_back(static_cast<char>(0x80 | ((code_point >> 6) & 0x3F)));
      value.push_back(static_cast<char>(0x80 | (code_point & 0x3F)));
    }
  }

  void skip_object() {
    expect('{');
    if (consume('}')) {
      return;
    }

    while (true) {
      static_cast<void>(parse_string());
      expect(':');
      skip_value();
      if (consume('}')) {
        return;
      }

      expect(',');
    }
  }

  void skip_array() {
    expect('[');
    if (consume(']')) {
      return;
    }

    while (true) {
      skip_value();
      if (consume(']')) {
        return;
      }

      expect(',');
    }
  }

  void consume_literal(std::string_view literal) {
    if (input_.substr(position_, literal.size()) != literal) {
      throw ProtocolError("invalid JSON literal");
    }

    position_ += literal.size();
  }

  void skip_number() {
    skip_whitespace();
    const size_t start = position_;
    if (position_ < input_.size() && input_[position_] == '-') {
      position_++;
    }

    if (position_ == input_.size() || !std::isdigit(static_cast<unsigned char>(input_[position_]))) {
      throw ProtocolError("invalid JSON value");
    }

    if (input_[position_] == '0') {
      position_++;
    } else {
      while (position_ < input_.size() && std::isdigit(static_cast<unsigned char>(input_[position_]))) {
        position_++;
      }
    }

    if (position_ < input_.size() && input_[position_] == '.') {
      position_++;
      const size_t fraction_start = position_;
      while (position_ < input_.size() && std::isdigit(static_cast<unsigned char>(input_[position_]))) {
        position_++;
      }

      if (position_ == fraction_start) {
        throw ProtocolError("invalid JSON number");
      }
    }

    if (position_ < input_.size() && (input_[position_] == 'e' || input_[position_] == 'E')) {
      position_++;
      if (position_ < input_.size() && (input_[position_] == '+' || input_[position_] == '-')) {
        position_++;
      }

      const size_t exponent_start = position_;
      while (position_ < input_.size() && std::isdigit(static_cast<unsigned char>(input_[position_]))) {
        position_++;
      }

      if (position_ == exponent_start) {
        throw ProtocolError("invalid JSON number");
      }
    }

    if (position_ == start) {
      throw ProtocolError("invalid JSON number");
    }
  }

  std::string_view input_;
  size_t position_ = 0;
};

void require_range(int value, int minimum, int maximum, std::string_view name) {
  if (value < minimum || value > maximum) {
    throw ProtocolError(std::string(name) + " is outside the supported range");
  }
}

void append_json_string(std::ostringstream& output, std::string_view value) {
  output << '"';
  for (const unsigned char character : value) {
    switch (character) {
      case '"': output << "\\\""; break;
      case '\\': output << "\\\\"; break;
      case '\b': output << "\\b"; break;
      case '\f': output << "\\f"; break;
      case '\n': output << "\\n"; break;
      case '\r': output << "\\r"; break;
      case '\t': output << "\\t"; break;
      default:
        if (character < 0x20U) {
          constexpr char hexadecimal[] = "0123456789abcdef";
          output << "\\u00" << hexadecimal[(character >> 4U) & 0x0FU]
                 << hexadecimal[character & 0x0FU];
        } else {
          output << static_cast<char>(character);
        }
        break;
    }
  }

  output << '"';
}

}  // namespace

SolveRequest parse_request(const std::string& json) {
  if (json.size() > kMaximumRequestBytes) {
    throw ProtocolError("request exceeds the 64 MiB protocol limit");
  }

  JsonReader reader(json);
  reader.expect('{');

  std::optional<int> protocol_version;
  std::optional<std::string> request_id;
  std::optional<int> variable_count;
  std::optional<std::vector<std::vector<int>>> clauses;
  std::optional<std::vector<std::vector<int>>> exactly_one_groups;
  std::optional<int> max_solutions;
  std::optional<int> timeout_milliseconds;

  if (!reader.consume('}')) {
    while (true) {
      const std::string key = reader.parse_string();
      reader.expect(':');

      if (key == "protocol_version") {
        if (protocol_version.has_value()) {
          throw ProtocolError("protocol_version is duplicated");
        }

        protocol_version = reader.parse_integer();
      } else if (key == "request_id") {
        if (request_id.has_value()) {
          throw ProtocolError("request_id is duplicated");
        }

        request_id = reader.parse_string();
      } else if (key == "variable_count") {
        if (variable_count.has_value()) {
          throw ProtocolError("variable_count is duplicated");
        }

        variable_count = reader.parse_integer();
      } else if (key == "clauses") {
        if (clauses.has_value()) {
          throw ProtocolError("clauses is duplicated");
        }

        std::vector<std::vector<int>> parsed_clauses;
        size_t literal_count = 0;
        reader.expect('[');
        if (!reader.consume(']')) {
          while (true) {
            if (parsed_clauses.size() == kMaximumClauses) {
              throw ProtocolError("clause count exceeds the protocol limit");
            }

            std::vector<int> clause;
            reader.expect('[');
            if (!reader.consume(']')) {
              while (true) {
                if (literal_count == kMaximumLiterals) {
                  throw ProtocolError("literal count exceeds the protocol limit");
                }

                clause.push_back(reader.parse_integer());
                literal_count++;
                if (reader.consume(']')) {
                  break;
                }

                reader.expect(',');
              }
            }

            parsed_clauses.push_back(std::move(clause));
            if (reader.consume(']')) {
              break;
            }

            reader.expect(',');
          }
        }

        clauses = std::move(parsed_clauses);
      } else if (key == "max_solutions") {
        if (max_solutions.has_value()) {
          throw ProtocolError("max_solutions is duplicated");
        }

        max_solutions = reader.parse_integer();
      } else if (key == "exactly_one_groups") {
        if (exactly_one_groups.has_value()) {
          throw ProtocolError("exactly_one_groups is duplicated");
        }

        std::vector<std::vector<int>> parsed_groups;
        size_t member_count = 0;
        reader.expect('[');
        if (!reader.consume(']')) {
          while (true) {
            if (parsed_groups.size() == kMaximumClauses) {
              throw ProtocolError("exactly_one_groups count exceeds the protocol limit");
            }

            std::vector<int> group;
            reader.expect('[');
            if (!reader.consume(']')) {
              while (true) {
                if (member_count == kMaximumLiterals) {
                  throw ProtocolError("exactly_one_groups member count exceeds the protocol limit");
                }

                group.push_back(reader.parse_integer());
                member_count++;
                if (reader.consume(']')) {
                  break;
                }

                reader.expect(',');
              }
            }

            parsed_groups.push_back(std::move(group));
            if (reader.consume(']')) {
              break;
            }

            reader.expect(',');
          }
        }

        exactly_one_groups = std::move(parsed_groups);
      } else if (key == "timeout_milliseconds") {
        if (timeout_milliseconds.has_value()) {
          throw ProtocolError("timeout_milliseconds is duplicated");
        }

        timeout_milliseconds = reader.parse_integer();
      } else {
        reader.skip_value();
      }

      if (reader.consume('}')) {
        break;
      }

      reader.expect(',');
    }
  }

  if (!reader.at_end()) {
    throw ProtocolError("unexpected data after request JSON");
  }

  if (!protocol_version.has_value() || !request_id.has_value() || !variable_count.has_value() ||
      !clauses.has_value() || !exactly_one_groups.has_value() || !max_solutions.has_value() ||
      !timeout_milliseconds.has_value()) {
    throw ProtocolError("request is missing a required field");
  }

  if (*protocol_version != kProtocolVersion) {
    throw ProtocolError("unsupported protocol_version");
  }

  if (request_id->empty() || request_id->size() > kMaximumRequestIdLength) {
    throw ProtocolError("request_id must contain between 1 and 128 characters");
  }

  require_range(*variable_count, 0, kMaximumVariables, "variable_count");
  require_range(*max_solutions, 1, kMaximumSolutions, "max_solutions");
  require_range(*timeout_milliseconds, 1, kMaximumTimeoutMilliseconds, "timeout_milliseconds");

  const auto estimated_response_bytes = 512ULL +
      static_cast<unsigned long long>(*variable_count) * 12ULL *
          static_cast<unsigned long long>(*max_solutions);
  if (estimated_response_bytes > kMaximumResponseBytes) {
    throw ProtocolError("requested model set exceeds the native response size limit");
  }

  for (const auto& clause : *clauses) {
    for (const int literal : clause) {
      if (literal == 0 || literal == std::numeric_limits<int>::min() ||
          std::abs(literal) > *variable_count) {
        throw ProtocolError("clause literal is outside variable_count");
      }
    }
  }

  std::vector<bool> grouped_variables(static_cast<size_t>(*variable_count) + 1U, false);
  for (const auto& group : *exactly_one_groups) {
    if (group.empty()) {
      throw ProtocolError("exactly_one_groups cannot contain an empty group");
    }

    for (const int variable : group) {
      if (variable <= 0 || variable > *variable_count || grouped_variables[static_cast<size_t>(variable)]) {
        throw ProtocolError("exactly_one_groups must contain each variable at most once");
      }

      grouped_variables[static_cast<size_t>(variable)] = true;
    }
  }

  if (!exactly_one_groups->empty() &&
      std::find(grouped_variables.begin() + 1, grouped_variables.end(), false) != grouped_variables.end()) {
    throw ProtocolError("exactly_one_groups must contain every variable when supplied");
  }

  return SolveRequest(
      *request_id,
      *variable_count,
      std::move(*clauses),
      std::move(*exactly_one_groups),
      *max_solutions,
      std::chrono::milliseconds(*timeout_milliseconds));
}

const char* status_name(SolveStatus status) {
  switch (status) {
    case SolveStatus::Feasible: return "feasible";
    case SolveStatus::Infeasible: return "infeasible";
    case SolveStatus::TimedOut: return "timed_out";
    case SolveStatus::InvalidRequest: return "invalid_request";
    case SolveStatus::InternalError: return "internal_error";
  }

  return "internal_error";
}

std::string serialize_response(const SolveResponse& response) {
  std::ostringstream output;
  output << "{\"protocol_version\":" << kProtocolVersion << ",\"request_id\":";
  append_json_string(output, response.request_id);
  output << ",\"status\":";
  append_json_string(output, status_name(response.status));
  output << ",\"solutions\":[";
  for (size_t solution_index = 0; solution_index < response.solutions.size(); solution_index++) {
    if (solution_index > 0U) {
      output << ',';
    }

    output << '[';
    const auto& solution = response.solutions[solution_index];
    for (size_t literal_index = 0; literal_index < solution.size(); literal_index++) {
      if (literal_index > 0U) {
        output << ',';
      }

      output << solution[literal_index];
    }

    output << ']';
  }

  output << "],\"metrics\":{\"elapsed_milliseconds\":" << response.metrics.elapsed_milliseconds
         << ",\"solve_calls\":" << response.metrics.solve_calls << "},\"message\":";
  append_json_string(output, response.message);
  output << '}';
  return output.str();
}

}  // namespace scheduler::protocol
