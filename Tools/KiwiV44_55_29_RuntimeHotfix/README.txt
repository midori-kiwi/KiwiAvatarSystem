v44.55.29 Runtime dependency hotfix

The vendor v29 runner enables v25 and v27 but removes
KIWI_V44_55_20_COMMON_TENSOR_AUDIT. v25 TryBindDependencies requires exactly
one v20 instance before it can bind its Lane metadata. The first v29
DIRECT_GRAPHICS run therefore produced v25 DEPENDENCY_BIND_TIMEOUT, v27 zero
pairs, and auto-quit exit 29.

This runner enables the same v20 120-second/1 Hz/8-second-stable settings used
by the completed v28 measurements, equally in all three arms. v20 is marked
dependency-only and is not used as v29 claim authority. All Production C#,
observer C#, models, Native DLLs, thresholds, and execution modes are unchanged.
