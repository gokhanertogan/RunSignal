from __future__ import annotations

import threading

import pytest
from apscheduler.schedulers.background import BackgroundScheduler
from apscheduler.triggers.cron import CronTrigger

from runsignal import Attributes, Metrics
from runsignal.apscheduler import cron_of, instrument


@pytest.mark.parametrize(
    "trigger, expected",
    [
        (CronTrigger(hour=2), "0 2 * * *"),
        (CronTrigger(minute="*/5"), "*/5 * * * *"),
        (CronTrigger(day_of_week="mon-fri", hour=6, minute=30), "30 6 * * 1-5"),
        (CronTrigger(day_of_week="sat,sun", hour=9, minute=0), "0 9 * * 6,7"),
        (CronTrigger(day_of_week="0-4", hour=1, minute=0), "0 1 * * 1-5"),  # APScheduler: 0 = Monday
        (CronTrigger(day_of_week="*/2", hour=1, minute=0), "0 1 * * 1-7/2"),
        (CronTrigger(minute=0, second=30), "30 0 * * * *"),
        (CronTrigger(day="last", hour=3, minute=0), "0 3 L * *"),
        (CronTrigger(year=2027), None),
        (CronTrigger(day="1st mon"), None),
    ],
)
def test_cron_triggers_become_unix_cron(trigger: CronTrigger, expected: str | None) -> None:
    assert cron_of(trigger) == expected


def test_jobs_are_registered_with_their_schedule_and_every_run_is_recorded(harness) -> None:
    h = harness()
    scheduler = BackgroundScheduler()
    calls = []

    def generate_invoices(count: int) -> None:
        calls.append(count)

    def import_statement() -> None:
        raise LookupError("no statement")

    scheduler.add_job(generate_invoices, "cron", hour=2, minute=0, id="Nightly Invoice", args=[3])
    scheduler.add_job(import_statement, "interval", minutes=5, name="ImportStatement")

    instrument(scheduler, registry=h.registry, workloads={"nightly-invoice": {"severity": "critical"}})

    assert h.registry.manifest("billing")["workloads"] == [
        {"name": "import-statement", "severity": "warning"},
        {"name": "nightly-invoice", "schedule": "0 2 * * *", "severity": "critical"},
    ]

    # Fire both jobs the way the scheduler does, with their stored arguments.
    for job in scheduler.get_jobs():
        try:
            job.func(*job.args, **job.kwargs)
        except LookupError:
            pass

    assert calls == [3]
    runs = {(p.attributes[Attributes.WORKLOAD], p.attributes[Attributes.OUTCOME]) for p in h.collect()[Metrics.RUNS]}
    assert runs == {("nightly-invoice", "success"), ("import-statement", "failure")}


def test_jobs_added_while_running_are_instrumented(harness) -> None:
    h = harness()
    scheduler = BackgroundScheduler()
    instrument(scheduler, registry=h.registry)
    scheduler.start()
    ran = threading.Event()
    try:
        scheduler.add_job(ran.set, id="ping")  # runs once, now
        assert ran.wait(5)
        scheduler.shutdown(wait=True)
    finally:
        if scheduler.running:
            scheduler.shutdown(wait=False)

    runs = h.collect()[Metrics.RUNS]
    assert [(p.attributes[Attributes.WORKLOAD], p.attributes[Attributes.OUTCOME]) for p in runs] == [("ping", "success")]
