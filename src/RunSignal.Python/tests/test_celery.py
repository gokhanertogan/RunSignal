from __future__ import annotations

import pytest
from celery import Celery
from celery.schedules import crontab

from runsignal import Attributes, Metrics
from runsignal.celery import beat_cron, discover_beat_schedule, instrument, workload_of


class StatementMissing(Exception):
    error_type = "statement_missing"


@pytest.fixture
def app() -> Celery:
    app = Celery("billing", set_as_current=False)
    app.conf.task_always_eager = True
    return app


def test_crontabs_become_unix_cron() -> None:
    assert beat_cron(crontab(hour=2, minute=0)) == "0 2 * * *"
    assert beat_cron(crontab(minute="*/15", day_of_week="mon-fri")) == "*/15 * * * mon-fri"
    assert beat_cron(crontab(minute={30, 0}, hour=6, day_of_week=0)) == "0,30 6 * * 0"


def test_beat_schedule_tasks_are_registered_before_they_run(harness, app) -> None:
    h = harness()
    app.conf.beat_schedule = {
        "nightly": {"task": "billing.tasks.generate_invoices", "schedule": crontab(hour=2, minute=0)},
        "every-minute": {"task": "billing.tasks.sync_stock", "schedule": 60.0},
        "cleanup": {"task": "celery.backend_cleanup", "schedule": crontab(hour=4, minute=0)},
    }

    discover_beat_schedule(app, registry=h.registry, workloads={
        "billing.tasks.generate_invoices": {"severity": "critical", "owner": "billing-team",
                                            "runbook_url": "https://wiki.example.com/invoices"},
    })

    assert h.registry.manifest("billing")["workloads"] == [
        {"name": "billing.tasks.generate_invoices", "schedule": "0 2 * * *", "severity": "critical",
         "owner": "billing-team", "runbookUrl": "https://wiki.example.com/invoices"},
        {"name": "billing.tasks.sync_stock", "severity": "warning"},
    ]


def test_task_runs_are_recorded_with_no_code_in_the_tasks(harness, app) -> None:
    h = harness()
    instrument(registry=h.registry)

    @app.task(name="billing.tasks.generate_invoices", bind=True)
    def generate_invoices(self, count: int) -> int:
        workload_of(self).processed(count)
        return count

    @app.task(name="billing.tasks.import_statement")
    def import_statement() -> None:
        raise StatementMissing("no statement today")

    assert generate_invoices.delay(3).get() == 3
    with pytest.raises(StatementMissing):
        import_statement.delay().get()

    m = h.collect()
    runs = {(p.attributes[Attributes.WORKLOAD], p.attributes[Attributes.OUTCOME], p.attributes.get(Attributes.ERROR_TYPE)): p.value
            for p in m[Metrics.RUNS]}
    assert runs == {
        ("billing.tasks.generate_invoices", "success", None): 1,
        ("billing.tasks.import_statement", "failure", "statement_missing"): 1,
    }
    assert m[Metrics.ITEMS][0].value == 3


def test_finished_and_failed_tasks_leave_no_run_in_progress(harness, app) -> None:
    h = harness()
    instrument(registry=h.registry)

    @app.task(name="billing.tasks.retry_stock", bind=True, max_retries=1)
    def retry_stock(self) -> None:
        self.retry(exc=StatementMissing("not yet"), countdown=0)

    with pytest.raises(StatementMissing):
        retry_stock.delay().get()

    ages = {p.attributes[Attributes.WORKLOAD]: p.value for p in h.collect()[Metrics.ACTIVE_RUN_AGE]}
    assert ages == {"billing.tasks.retry_stock": 0}


def test_a_task_id_that_starts_twice_ends_both_runs(harness, app) -> None:
    from runsignal.celery import _Instrumentation

    h = harness()
    handler = _Instrumentation(h.registry, {})

    @app.task(name="billing.tasks.redelivered")
    def redelivered() -> None:
        pass

    handler.prerun(task_id="t1", task=redelivered)
    handler.prerun(task_id="t1", task=redelivered)
    handler.postrun(task_id="t1", state="SUCCESS")
    handler.revoked(request=type("Request", (), {"id": "t1"})())

    ages = {p.attributes[Attributes.WORKLOAD]: p.value for p in h.collect()[Metrics.ACTIVE_RUN_AGE]}
    assert ages == {"billing.tasks.redelivered": 0}
    assert handler.runs == {}
