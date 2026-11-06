#!/usr/bin/env bash
# The complete shell comparison is retained in docs/validation/preflight-1593.md.
# Production preflight has one typed authority; remaining shell phases may still source this library.
deploy_run_preflight() {
    deploy_fail preflight authority use-typed-preflight
    return 1
}
