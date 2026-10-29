"""
RunSignal records runs, items and heartbeats of Python background work (Celery tasks, APScheduler jobs, cron
scripts, queue consumers, sync loops) as OpenTelemetry metrics and spans, so you can alert on work that no HTTP
status code describes: missed, failed, overrunning, stalled or low-output jobs.

The metric names, attributes and manifest format are the same in every RunSignal SDK, so one set of alert rules
covers .NET, Go, Java, Node.js, Python and Rust services.

    from runsignal import default_registry

    invoices = default_registry.workload("nightly-invoice", schedule="0 2 * * *", max_duration="30m",
                                         min_processed=100, severity="critical")

    with invoices.run():
        for order in unbilled_orders():
            create_invoice(order)
            invoices.processed()
"""

from ._naming import InvalidWorkloadNameError, to_workload_name
from ._registry import (
    Duration,
    Expectations,
    Run,
    Severity,
    Workload,
    WorkloadConfigurationError,
    WorkloadRegistry,
    default_registry,
    monitor,
)
from ._telemetry import DURATION_BUCKETS, SCOPE_NAME, VERSION, Attributes, Metrics

__version__ = VERSION

__all__ = [
    "Attributes",
    "DURATION_BUCKETS",
    "Duration",
    "Expectations",
    "InvalidWorkloadNameError",
    "Metrics",
    "Run",
    "SCOPE_NAME",
    "Severity",
    "VERSION",
    "Workload",
    "WorkloadConfigurationError",
    "WorkloadRegistry",
    "default_registry",
    "monitor",
    "to_workload_name",
]
