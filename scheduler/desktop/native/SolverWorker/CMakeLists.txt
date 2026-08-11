cmake_minimum_required(VERSION 3.25)

project(SolverWorker LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(SCHEDULER_CADICAL_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/third_party/cadical" CACHE PATH
    "Path to the pinned CaDiCaL source tree")
set(SCHEDULER_CADICAL_LIBRARY "${SCHEDULER_CADICAL_SOURCE_DIR}/build/libcadical.a" CACHE FILEPATH
    "Path to the statically linked CaDiCaL library built from the pinned source")

if(NOT EXISTS "${SCHEDULER_CADICAL_SOURCE_DIR}/src/cadical.hpp")
  message(FATAL_ERROR "Pinned CaDiCaL source is missing. See third_party/NOTICE.md.")
endif()

if(NOT EXISTS "${SCHEDULER_CADICAL_LIBRARY}")
  message(FATAL_ERROR
    "CaDiCaL library is missing at ${SCHEDULER_CADICAL_LIBRARY}. Build the pinned source before configuring SolverWorker.")
endif()

add_executable(SolverWorker
  src/main.cpp
  src/protocol.cpp
)
target_include_directories(SolverWorker PRIVATE "${SCHEDULER_CADICAL_SOURCE_DIR}/src")
target_link_libraries(SolverWorker PRIVATE "${SCHEDULER_CADICAL_LIBRARY}")

if(MSVC)
  target_compile_options(SolverWorker PRIVATE /W4 /WX /permissive-)
else()
  target_compile_options(SolverWorker PRIVATE -Wall -Wextra -Wpedantic -Werror)
endif()

enable_testing()
add_test(NAME solver_worker_version COMMAND SolverWorker --version)
add_test(NAME solver_worker_self_test COMMAND SolverWorker --self-test)
add_test(NAME solver_worker_protocol_self_test COMMAND SolverWorker --protocol-self-test)
