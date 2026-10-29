"""
Names of the meter, tracer, instruments and attributes RunSignal emits. They are identical in every RunSignal SDK
(see spec/README.md in the repository), so one set of alert rules covers every language.
"""

SCOPE_NAME = "RunSignal"
VERSION = "0.2.0"


class Metrics:
    RUNS = "runsignal.workload.runs"
    DURATION = "runsignal.workload.duration"
    ITEMS = "runsignal.workload.items"
    HEARTBEATS = "runsignal.workload.heartbeats"
    SINCE_SUCCESS = "runsignal.workload.since_success"
    SINCE_HEARTBEAT = "runsignal.workload.since_heartbeat"


class Attributes:
    WORKLOAD = "runsignal.workload"
    OUTCOME = "runsignal.outcome"
    ITEM_RESULT = "runsignal.item.result"
    ERROR_TYPE = "error.type"


# Histogram bucket boundaries, in seconds, for the duration metric: background work runs from milliseconds to
# hours, while the OpenTelemetry defaults stop at 10 seconds.
DURATION_BUCKETS = (
    0.01, 0.05, 0.1, 0.5, 1, 5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 28800,
)
