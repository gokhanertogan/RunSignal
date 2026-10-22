// Hot-path cost with the OpenTelemetry metrics SDK attached; the times include its aggregation.
// Run with: npm run bench
import { harness } from './harness.js';

function bench(name: string, iterations: number, fn: () => void): void {
  for (let i = 0; i < 10_000; i++) fn(); // warm up
  const start = process.hrtime.bigint();
  for (let i = 0; i < iterations; i++) fn();
  const ns = Number(process.hrtime.bigint() - start) / iterations;
  console.log(`${name.padEnd(28)} ${ns.toFixed(0).padStart(6)} ns/op`);
}

const traced = harness().registry.workload('benchmark');
const untraced = harness({ tracing: false }).registry.workload('benchmark');

bench('start + succeed (tracing off)', 1_000_000, () => untraced.start().succeed());
bench('start + succeed (traced)', 200_000, () => traced.start().succeed());
bench('processed', 2_000_000, () => untraced.processed());
bench('beat', 2_000_000, () => untraced.beat());
