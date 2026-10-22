package io.runsignal;

/** An exception can supply its own low-cardinality {@code error.type}, such as {@code "statement_missing"}. */
public interface ErrorTyped {
  String errorType();
}
