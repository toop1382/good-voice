# E2E Test Infra: Low-Latency Voice Chat System

## Test Philosophy
- Opaque-box, requirement-driven. No dependency on implementation design.
- Methodology: Category-Partition + BVA + Pairwise + Workload Testing.

## Feature Inventory
| # | Feature | Source (requirement) | Tier 1 | Tier 2 | Tier 3 |
|---|---------|---------------------|:------:|:------:|:------:|
| 1 | Room Session Management | ORIGINAL_REQUEST R2 (Room/Session Management) | 5 | 5 | ✓ |
| 2 | Audio Packet Broadcasting | ORIGINAL_REQUEST R2 (Broadcast encoded audio) | 5 | 5 | ✓ |
| 3 | Packet Formatting & Validation | PROJECT.md Interface Contracts | 5 | 5 | ✓ |
| 4 | Latency & Diagnostic Metrics | ORIGINAL_REQUEST R3 (Profiling Metrics) | 5 | 5 | ✓ |
| 5 | Robustness & Scale | ORIGINAL_REQUEST R2 & Acceptance Criteria | 5 | 5 | ✓ |

## Test Architecture
- Test runner: A .NET Console application or test runner located in `Tests/` that manages test execution. It spawns the Server process, initializes Mock Clients, sets up test assertions, and gathers execution logs.
- Test case format: Automated C# test cases executing using xUnit or a custom C# console test harness.
- Directory layout:
  - `Tests/`: Test runner project, test cases, and framework.
  - `MockClient/`: Client simulation library and CLI tools.

## Real-World Application Scenarios (Tier 4)
| # | Scenario | Features Exercised | Complexity |
|---|----------|--------------------|------------|
| 1 | Standard Chat Session | F1, F2, F3, F4 | Medium |
| 2 | High-Load Room | F1, F2, F5 | High |
| 3 | Multi-Room Office | F1, F2, F5 | High |
| 4 | Network Degradation | F2, F4, F5 | Medium |
| 5 | Graceful Teardown and Restart | F1, F5 | Low |

## Coverage Thresholds
- Tier 1: 25 test cases (5 per feature)
- Tier 2: 25 test cases (5 per feature)
- Tier 3: 5 cross-feature combination test cases
- Tier 4: 5 real-world application scenarios
