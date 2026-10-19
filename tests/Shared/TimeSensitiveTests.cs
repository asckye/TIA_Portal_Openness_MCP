using System;
using System.Collections.Generic;
using System.IO;

#if TIA_RELEASE_TEST_SCHEDULING
namespace TiaMcp.ReleaseTool
#else
namespace TiaMcp.Engine.Tests.Shared
#endif
{
    // Shared by the scheduler and executor so unknown/unmeasured cases receive
    // the same isolation as measured cases. These are execution partitions only.
    public static class TimeSensitiveTests
    {
        public static readonly IReadOnlyDictionary<string, string> Classes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["EngineHttpSessionTests"] = "Worker startup with scaled deadlines; bounded HTTP readiness/shutdown.",
            ["EngineHttpSessionTestsPlc"] = "PlcWorker variant of EngineHttpSessionTests: worker startup with scaled deadlines; bounded HTTP readiness/shutdown.",
            ["WorkerRobustnessTests"] ="Scaled worker budgets, dispatch cancellation and real timeout measurements.",
            ["PlcWorkerRobustnessTests"] = "One-second startup/default budgets and 1.3-second synthetic native calls.",
            ["PlcFixtureWorkerRobustnessTests"] = "Two-second worker startup/default budget and injected fault deadlines.",
            ["EngineWorkerClientTests"] = "Worker startup and disposal measured against ten/two-second bounds.",
            ["FoundationProcessLeaseTests"] = "Child readiness/exit bounds and durable-request overhead measurement.",
            ["FoundationChecks"] = "Indivisible legacy producer includes bounded adapter evaluation and FIFO fixture startup.",
            ["PortedPortalAdmissionTests"] = "Two-second queued worker lane acquisition.",
            ["ApprovalPrecheckTests"] = "Warm approval precheck latency samples.",
            ["FoundationCoverageRegressionTests"] = "Before/after symbol lookup latency samples.",
            ["PipelineTests"] = "Local tool p95 below 100 ms during a sixty-second worker call.",
            ["ConcurrencyTests"] = "Two-second queued worker lane acquisition.",
            ["OperatorProtocolTests"] = "One-second pipe connect and three-second graceful shutdown measurements.",
            ["OperatorSessionTests"] = "Bounded worker dispatch and MCP shutdown.",
            ["OperatorSessionTestsPlc"] = "PlcWorker variant of OperatorSessionTests: bounded worker dispatch and MCP shutdown.",
            ["SaveCloseCandidateTests"] ="Synthetic channel connect within three seconds.",
            ["TiaMcp.FoundationHost.Tests.SessionCandidateTests"] = "Synthetic channel connect within three seconds.",
            ["WorkbenchControlHostTests"] = "Pipe connect/response deadlines and bounded request deadline assertions.",
            ["TiaMcp.Engine.Tests.ApprovalPrecheckTests"] = "Warm approval precheck latency samples.",
            ["TiaMcp.Engine.Tests.ApprovalTests"] = "Bounded approval pipe connection and response.",
            ["TiaMcp.Engine.Tests.ImportStagingTests"] = "Timed file-lock release and bounded publication retries.",
            ["TiaMcp.Engine.Tests.OfflineChecks"] = "Indivisible legacy producer includes one-second synthetic child termination.",
            ["PromptRegistrationChecks"] = "Collectible fixture unload after ten 50 ms GC waits.",
            ["TiaMcp.WorkerChannel.Tests.ProcessTests"] = "Real process handshake and 700 ms terminal timeout budgets.",
            ["TiaMcp.WorkerChannel.Tests.ProtocolTests"] = "40 ms protocol deadlines and three-second channel budgets.",
            ["TiaMcp.WorkerChannel.Tests.PreviewRuleTests"] = "40 ms dispatch deadline.",
            ["TiaMcp.WorkerChannel.Tests.EngineProfileTests"] = "Bounded channel handshake.",
            ["TiaMcp.WorkbenchControl.Tests.PipeSecurityTests"] = "250 ms real pipe connection.",
            ["TiaOpenness.Core.Tests.BridgeChannelTests"] = "Synthetic bridge process startup, 500 ms timeout and bounded shutdown.",
            ["TiaOpenness.Core.Tests.GitWorkspaceDiffTests"] = "Temporary repository child startup/exit within thirty seconds.",
            ["TiaOpenness.Core.Tests.CallJournalTests"] = "Five-second live audit notification completion.",
            ["TiaOpenness.Core.Tests.IntegrationTests"] = "Synthetic bridge process connect/dispatch budgets.",
            ["TiaOpenness.Gui.Tests.ResponsivenessTests"] = "Reader throttling, latency measurement and bounded lock recovery.",
            ["TiaOpenness.Gui.Tests.ResponsivenessHarness"] = "Opt-in dispatcher p95/max latency and idle CPU thresholds.",
            ["TiaOpenness.Gui.Tests.WorkbenchControlLoadTests"] = "Dispatcher p95 below 50 ms and maximum below 200 ms.",
            ["TiaOpenness.Gui.Tests.ApprovalServiceTests"] = "One-second pipe connect and bounded approval delivery.",
            ["TiaOpenness.Gui.Tests.WorkbenchControlPipeTests"] = "One-second pipe connect and bounded response.",
            ["TiaOpenness.Gui.Tests.WorkbenchControlTests"] = "Blocked-dispatcher ordering and pipe deadlines.",
            ["TiaOpenness.Gui.Tests.McpPanelTests"] = "Real session expiry and bounded command completion.",
            ["TiaOpenness.Gui.Tests.WorkbenchMcpPrefillTests"] = "Bounded command completion polling.",
            ["TiaOpenness.Gui.Tests.NetworkHelperTests"] = "Synthetic helper process exit within sixty seconds.",
            ["TiaOpenness.Gui.Tests.UnifiedDesktopTests"] = "Ten-second asynchronous approval persistence.",
            ["TiaOpenness.Gui.Tests.VciPanelTests"] = "Five-second preview command admission.",
            ["TiaMcp.ShippedTools.Tests.WriteGuardPortTests"] = "Cold hook startup/decision within fifteen/twenty seconds.",
            ["TiaMcp.Updater.Tests.UpdaterTests"] = "Synthetic launcher/installed-process startup and exit bounds.",
            ["TiaMcp.SourceContracts.Tests.ScriptClientTests"] = "Five-second client cancellation completion.",
            ["TiaMcp.SourceContracts.Tests.ReadOnlySourceContracts"] = "Bounded child MSBuild evaluation.",
            ["TiaMcp.ReleaseTool.Tests.CheckAllRound3Tests"] = "Thirty-second child/grandchild affinity probe and ten-second barriers.",
            ["TiaMcp.ReleaseTool.Tests.CheckAllTests"] = "Concurrent scheduler peak/resource assertions around timed synthetic work.",
            ["TiaMcp.ReleaseTool.Tests.ReleaseValidationTests"] = "Five-second scheduler barriers.",
        };

        public static readonly IReadOnlyDictionary<string, string[]> Methods = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["EngineHttpSessionTests"] = new[] {
                "Production_catalog_rejects_fixture_tools_and_startup_reports_stderr",
                "Unknown_outcome_can_disconnect_and_a_new_session_can_acquire_the_same_lease",
                "Http_session_end_without_disconnect_releases_its_attached_worker",
                "Idle_attached_restart_detaches_before_starting_the_next_generation",
                "Busy_or_timed_out_disconnect_terminates_the_worker_and_keeps_the_lease_busy",
                "Restart_after_a_timeout_reports_a_dirty_lease",
                "Http_sessions_own_workers_disconnect_restart_and_teardown",
                "Session_fault_and_bridge_staging_owner_do_not_escape_the_session",
                "Authored_lease_refusal_is_sanitised_and_internal_exception_text_is_hidden",
                "Health_has_file_version_and_readiness_requires_authentication",
                "Shutdown_drains_an_open_http_request_and_stops_its_session_worker"
            },
            ["EngineHttpSessionTestsPlc"] = new[] {
                "Production_catalog_rejects_fixture_tools_and_startup_reports_stderr",
                "Unknown_outcome_can_disconnect_and_a_new_session_can_acquire_the_same_lease",
                "Http_session_end_without_disconnect_releases_its_attached_worker",
                "Idle_attached_restart_detaches_before_starting_the_next_generation",
                "Busy_or_timed_out_disconnect_terminates_the_worker_and_keeps_the_lease_busy",
                "Restart_after_a_timeout_reports_a_dirty_lease",
                "Http_sessions_own_workers_disconnect_restart_and_teardown",
                "Session_fault_and_bridge_staging_owner_do_not_escape_the_session",
                "Authored_lease_refusal_is_sanitised_and_internal_exception_text_is_hidden",
                "Health_has_file_version_and_readiness_requires_authentication",
                "Shutdown_drains_an_open_http_request_and_stops_its_session_worker"
            },
            ["WorkerRobustnessTests"] = new[] {
                "Faults_lock_both_pipelines_and_require_explicit_restart",
                "Cancellation_after_dispatch_keeps_the_late_result_and_journal",
                "Oversized_reply_survives_a_spill_slower_than_the_fault_injection_deadline",
                "First_attach_has_a_longer_deadline_and_the_access_confirmation_hint"
            },
            ["PlcWorkerRobustnessTests"] = new[] {
                "Plc_long_compile_and_import_complete_with_scaled_production_budgets",
                "Plc_scaled_long_call_still_fails_with_only_the_default_budget",
                "Plc_compile_preview_uses_default_instead_of_the_compile_override",
                "Plc_over_budget_call_is_unknown_and_locks_the_session",
                "Plc_default_seconds_and_first_attach_budget_apply_once_per_generation",
                "Plc_cancellation_before_dispatch_is_CANCELLED_and_after_dispatch_keeps_the_result"
            },
            ["EngineWorkerClientTests"] = new[] {
                "FailedExplicitRestartRetainsPriorLockAndNeverRestartsImplicitly",
                "Session_end_and_host_stopping_release_without_explicit_disconnect",
                "Stop_during_native_dispatch_or_after_unknown_retains_refusal"
            },
            ["FoundationProcessLeaseTests"] = new[] {
                "Process_gone_is_refused_without_native_dispatch",
                "Job_object_killed_idle_worker_allows_takeover_with_client_evidence",
                "Job_kill_during_native_fixture_keeps_busy_and_refuses_takeover",
                "Host_process_exit_runs_graceful_stop_before_its_job_closes",
                "Durable_request_state_overhead_is_measured",
                "Two_workers_racing_to_attach_have_one_winner_and_clean_release_allows_retry",
                "Worker_stdin_eof_releases_an_attached_session_before_the_next_worker"
            },
            ["FoundationChecks"] = new[] {
                "Check"
            },
            ["PortedPortalAdmissionTests"] = new[] {
                "Port_dispatch_takes_the_shared_worker_lane_before_journaling"
            },
            ["ApprovalPrecheckTests"] = new[] {
                "Preview_then_approval_then_real_precondition_with_disabled_bypass",
                "Warm_fixture_reports_added_preview_latency"
            },
            ["FoundationCoverageRegressionTests"] = new[] {
                "Exact_duplicate_in_a_later_table_avoids_probes_and_scans_while_case_only_duplicates_remain_rejected"
            },
            ["PipelineTests"] = new[] {
                "LocalLaneRemainsBoundedAtEight",
                "IndependentSessionsDoNotShareWorkerLane",
                "ApprovalWaitDoesNotOccupyWorkerLane",
                "LocalToolsStayBelow100MsP95DuringA60SecondWorkerCall"
            },
            ["ConcurrencyTests"] = new[] {
                "Worker_lanes_are_independent_and_queued_cancellation_releases_no_extra_permit",
                "Renderer_has_no_worker_lane_even_when_that_worker_is_busy",
                "Native_tool_waits_for_worker_and_reentrant_scope_does_not_reacquire"
            },
            ["OperatorProtocolTests"] = new[] {
                "Failed_session_creation_is_reported_before_the_handshake_read_can_hang",
                "Ai_host_refuses_create_and_unknown_session_or_version",
                "Slow_owned_session_shutdown_precedes_endpoint_exit_and_http_keeps_serving",
                "Owner_creates_a_private_session_and_pipe_close_releases_it",
                "Windows_peer_process_token_and_exact_image_are_checked_before_read"
            },
            ["OperatorSessionTests"] = new[] {
                "Operator_stream_shares_one_existing_worker_and_disconnect_state",
                "Ai_disconnect_and_operator_detach_have_distinct_lifetimes",
                "Lock_and_restart_belong_to_the_attached_session",
                "Actual_worker_lane_orders_operator_and_ai_and_cancels_only_queued_call",
                "Operator_write_uses_existing_approval_frame_and_cannot_spoof_actor",
                "Queued_ai_refusal_does_not_replace_the_operator_who_ended_the_session",
                "Engine_operator_view_does_not_forge_or_accept_another_ai_session_id",
                "Attached_panel_and_ai_share_generation_fence_before_approval",
                "Host_shutdown_releases_active_generation_fence",
                "A_write_approved_before_fence_acquisition_is_rechecked_before_dispatch"
            },
            ["OperatorSessionTestsPlc"] = new[] {
                "Operator_stream_shares_one_existing_worker_and_disconnect_state",
                "Ai_disconnect_and_operator_detach_have_distinct_lifetimes",
                "Lock_and_restart_belong_to_the_attached_session",
                "Actual_worker_lane_orders_operator_and_ai_and_cancels_only_queued_call",
                "Operator_write_uses_existing_approval_frame_and_cannot_spoof_actor",
                "Queued_ai_refusal_does_not_replace_the_operator_who_ended_the_session",
                "Engine_operator_view_does_not_forge_or_accept_another_ai_session_id",
                "Attached_panel_and_ai_share_generation_fence_before_approval",
                "Host_shutdown_releases_active_generation_fence",
                "A_write_approved_before_fence_acquisition_is_rechecked_before_dispatch"
            },
            ["SaveCloseCandidateTests"] = new[] {
                "ChannelAcceptsOnlyThePlannedBindingEpochTransition"
            },
            ["TiaMcp.FoundationHost.Tests.SessionCandidateTests"] = new[] {
                "ProjectOrPidReuseDuringAttachIsUnknown",
                "MalformedWorkerReasonRetainsUnknownEnvelopeAndReset",
                "OriginalTreeChangedDuringUpgradePoisonsSession"
            },
            ["WorkbenchControlHostTests"] = new[] {
                "Missing_response_times_out_at_the_correct_stage",
                "Real_fake_workbench_server_receives_each_operation_with_matching_process_identity"
            },
            ["TiaMcp.Engine.Tests.ApprovalPrecheckTests"] = new[] {
                "Valid_bridge_waits_once_and_real_call_rechecks_while_disabled_skips_preview",
                "Warm_fixture_reports_added_preview_latency"
            },
            ["TiaMcp.Engine.Tests.ApprovalTests"] = new[] {
                "Dry_run_writes_do_not_queue_for_approval",
                "Session_save_and_close_targets_are_gated_but_connect_and_disconnect_are_not",
                "CallTool_write_target_refuses_before_dispatch_when_workbench_is_unavailable",
                "Disabled_approval_warning_covers_bridge_and_batch_write_exits",
                "File_export_cannot_disable_approval_even_when_ordinary_file_tools_are_not_gated",
                "Engine_write_audit_uses_response_id_and_starts_only_after_approval",
                "Engine_refuses_retired_host_modes",
                "Approval_frames_refuse_oversize_truncation_and_self_decision_shape",
                "A_decision_cannot_authorize_another_request_or_plan",
                "Applied_batch_gates_each_write_while_internal_previews_remain_read_only",
                "Confirmation_details_are_closed_and_roundtrip",
                "Digest_binds_identity_and_all_arguments_and_redacts_display_only",
                "Pipe_decision_is_bound_once",
                "Absent_and_timeout_are_not_started_and_audited"
            },
            ["TiaMcp.Engine.Tests.ImportStagingTests"] = new[] {
                "Manifest_replace_retries_transient_delete_sharing_locks_and_keeps_partial_batches_cleanable"
            },
            ["TiaMcp.Engine.Tests.OfflineChecks"] = new[] {
                "Check"
            },
            ["PromptRegistrationChecks"] = new[] {
                "Check"
            },
            ["TiaMcp.WorkerChannel.Tests.ProcessTests"] = new[] {
                "DuplicateHelloTerminal",
                "HandshakeFailedBeforeWrites",
                "TerminalAmbiguityNoReplay",
                "First_attach_timeout_explains_the_TIA_access_confirmation",
                "First_attach_timeout_is_identified_after_an_earlier_read",
                "TrailingFramesPoisonIdlePeer",
                "ReadFailureOutcomePreserved",
                "UnsentCancellationStaysUsable",
                "ProgressAcceptedAndDistinctOwnedHandles",
                "ConcurrentEndpointPoisoned"
            },
            ["TiaMcp.WorkerChannel.Tests.ProtocolTests"] = new[] {
                "Answered_unknown_keeps_project_requests_refused_and_disconnect_available",
                "Answered_project_unknown_allows_the_independent_simulator_without_clearing_the_latch",
                "BadHelloPoisonsSession",
                "DuplicateHelloTerminal",
                "MissingHelloHasNoSentOperation",
                "OversizedLinePoisonsBeforeParsing",
                "ExactFrameCapAccepted",
                "CancelBeforeDispatchHasNoSendOrPoison",
                "ConcurrentEndpointPoisoned",
                "UnknownResultPoisonsHostNoReplay",
                "UnknownReplyIdOrBindingEpochPoisons",
                "LateProgressAndTrailingFramesPoison",
                "InvalidProgressTerminal",
                "BudgetNeverResetsOnProgressOrCleanup",
                "ReadFailureOutcomePreserved",
                "CloseAdvancesBindingEpoch",
                "ProgressAcceptedAndDtoBytesPreserved"
            },
            ["TiaMcp.WorkerChannel.Tests.PreviewRuleTests"] = new[] {
                "UncooperativeWriteIsBoundedAndNeverReplayed",
                "FragmentedUtf8RequestAndExactByteCap",
                "ExactReleaseHelloRoundTrip",
                "StrictEnvelopeRejectedBeforeDispatchOrResult",
                "ExplicitNullAndReadErrorCodePreserved",
                "KnownFailureCannotChangeBindingEpoch",
                "ProgressCapAndPerRequestSequence"
            },
            ["TiaMcp.WorkerChannel.Tests.EngineProfileTests"] = new[] {
                "Latched_engine_project_still_permits_the_independent_simulator",
                "RequestLimitDoesNotConsumeIdOrPoisonClient",
                "ClientForwardsProgressAndUnknownToolReply",
                "Foundation_and_engine_share_one_owner_epoch_and_correlation",
                "EpochRules",
                "UnknownToolOutcomeKeepsServerUsable",
                "ChannelUnknownPermitsOnlyARecoveryDisconnect",
                "OversizedReplyUsesLimitEnvelopeAndAcceptsNextRequest",
                "ProgressObjectStaysOnOwnerThread"
            },
            ["TiaMcp.WorkbenchControl.Tests.PipeSecurityTests"] = new[] {
                "BothEndpointsRequireTheCurrentUser",
                "BothEndpointsRequireTheExactResolvedImage"
            },
            ["TiaOpenness.Core.Tests.BridgeChannelTests"] = new[] {
                "Chinese_stderr_round_trips_through_the_real_bridge_start_and_log_reader",
                "Mock_hello_refuses_wrong_identity_without_sending_a_call",
                "Every_Studio_error_keeps_its_code_message_and_diagnostics",
                "Cancellation_before_dispatch_does_not_send_or_consume_an_id",
                "Mock_result_keeps_the_previous_client_date_conversion",
                "Killed_bridge_is_never_restarted_or_replayed",
                "Failure_after_dispatch_faults_the_session_without_replay",
                "Bridge_killed_during_mock_import_is_not_replayed",
                "Dispose_ends_the_owned_mock_bridge",
                "Handled_Studio_errors_preserve_the_outcome_and_keep_the_same_bridge_usable",
                "Raw_error_data_and_progress_payload_survive_the_channel_envelope"
            },
            ["TiaOpenness.Core.Tests.GitWorkspaceDiffTests"] = new[] {
                "Tracked_block_edit_reports_added_and_removed_lines",
                "New_mapped_block_without_extension_resolves_actual_unicode_filename",
                "Whole_workspace_shows_new_files_alongside_tracked_changes",
                "Staged_only_block_is_visible_before_the_first_commit",
                "Relative_workspace_path_still_reads_the_real_repository"
            },
            ["TiaOpenness.Core.Tests.CallJournalTests"] = new[] {
                "Live_polling_and_disposal_follow_file_changes"
            },
            ["TiaOpenness.Core.Tests.IntegrationTests"] = new[] {
                "Native_contract_connects_opens_browses_exports_imports_compiles_saves_and_disconnects",
                "Native_contract_runs_workspace_preview_map_status_sync_and_local_diff",
                "Native_errors_do_not_break_the_next_normal_request",
                "Doctor_runs_through_real_bridge_without_loading_a_native_session"
            },
            ["TiaOpenness.Gui.Tests.ResponsivenessTests"] = new[] {
                "Idle_services_do_not_notify_and_repeated_reads_keep_rows_and_redacted_json",
                "Bursts_coalesce_and_hidden_pages_do_not_project_calls",
                "Continuous_changes_are_throttled_without_starving_the_reader_and_inactive_readers_sleep",
                "Contended_audit_and_retention_locks_do_not_block_dispatcher_actions"
            },
            ["TiaOpenness.Gui.Tests.ResponsivenessHarness"] = new[] {
                "Measure_dispatcher_under_fake_engine_load"
            },
            ["TiaOpenness.Gui.Tests.WorkbenchControlLoadTests"] = new[] {
                "Continuous_control_queue_keeps_existing_dispatcher_latency_budget"
            },
            ["TiaOpenness.Gui.Tests.ApprovalServiceTests"] = new[] {
                "Workbench_v2_write_stays_pending_until_the_same_approval_card_is_decided",
                "Real_queue_decides_each_request_and_receives_execution_outcome",
                "Mcp_side_cannot_decide_and_closing_workbench_refuses_pending_call",
                "Real_settings_survive_reopen_and_log_switches"
            },
            ["TiaOpenness.Gui.Tests.WorkbenchControlPipeTests"] = new[] {
                "Real_pipe_and_real_window_support_each_operation",
                "Real_pipe_refuses_identity_protocol_and_UI_guard_failures",
                "Malformed_first_frame_is_closed_without_impersonating_or_applying"
            },
            ["TiaOpenness.Gui.Tests.WorkbenchControlTests"] = new[] {
                "Snapshot_is_frozen_paginated_and_reads_while_dispatcher_is_blocked",
                "Queue_is_bounded_FIFO_and_per_origin_inflight_is_limited",
                "Expired_and_closing_queued_requests_never_apply",
                "Started_requests_report_the_actual_result_after_the_deadline"
            },
            ["TiaOpenness.Gui.Tests.McpPanelTests"] = new[] {
                "Every_write_previews_then_waits_for_a_separate_approval",
                "Notifications_merge_by_revision_and_keep_endedBy_without_following_a_new_key",
                "Owned_session_waits_for_graceful_exit_on_detach_and_dispose",
                "Owned_session_dispose_joins_a_pending_detach",
                "Owned_session_forces_only_after_the_shutdown_timeout_and_traces_it",
                "Workbench_write_opens_the_normal_approval_card_and_requires_its_decision",
                "Cancel_ends_only_the_panels_pending_call",
                "Candidate_policy_uses_the_advertised_schema_and_preserves_the_preview_hash",
                "Source_export_uses_only_its_declared_arguments_and_waits_for_approval",
                "Background_session_end_updates_the_window_and_clears_its_project",
                "Page_commands_use_the_shared_session_and_preserve_the_V4_result",
                "Real_window_page_commands_render_envelopes_and_attach_cached_binding"
            },
            ["TiaOpenness.Gui.Tests.WorkbenchMcpPrefillTests"] = new[] {
                "Registered_execution_commands_are_attributed_MCP_panel_commands",
                "Human_execution_of_prefilled_MCP_forms_previews_and_requires_the_normal_approval"
            },
            ["TiaOpenness.Gui.Tests.NetworkHelperTests"] = new[] {
                "Network_helper_reports_bad_arguments_and_exits_without_crashing",
                "Network_helper_rejects_a_wrong_argument_count"
            },
            ["TiaOpenness.Gui.Tests.UnifiedDesktopTests"] = new[] {
                "Dispatcher_idle_does_not_complete_an_approval_save"
            },
            ["TiaOpenness.Gui.Tests.VciPanelTests"] = new[] {
                "Registered_older_release_tools_use_MCP_reads_and_approval_gated_writes",
                "Older_real_window_registered_reads_and_writes_are_enabled_and_writes_require_approval",
                "Engine_reads_preserve_V4_receipts_and_populate_the_real_page",
                "Every_engine_write_previews_and_waits_for_normal_workbench_approval",
                "Dry_run_is_only_preview_and_generation_fence_refusal_is_not_retried",
                "Invalid_generic_creation_preview_is_not_an_apply_permission"
            },
            ["TiaMcp.ShippedTools.Tests.WriteGuardPortTests"] = new[] {
                "Built_executable_reads_real_stdin_and_writes_the_hook_protocol_to_stdout"
            },
            ["TiaMcp.Updater.Tests.UpdaterTests"] = new[] {
                "Built_updater_applies_a_local_package_and_rolls_it_back",
                "Running_bundle_engine_is_listed_and_refused",
                "Wait_for_workbench_pid_times_out_after_30_seconds",
                "Legacy_33_package_preflight_refuses_v4_layout_before_mutating_install"
            },
            ["TiaMcp.SourceContracts.Tests.ScriptClientTests"] = new[] {
                "ConfiguredTimeoutAppliesThroughTheSharedClientInterface"
            },
            ["TiaMcp.SourceContracts.Tests.ReadOnlySourceContracts"] = new[] {
                "SupplementaryReleaseSymbols"
            },
            ["TiaMcp.ReleaseTool.Tests.CheckAllRound3Tests"] = new[] {
                "IsolatedLanesOverlapEvenWhenGeneralWorkIsExclusive",
                "AffinityJobIncludesClientChildAndImmediatelySpawnedGrandchild",
                "AffinityJobWaitsForOwnedDescendantsAfterTheirParentExits"
            },
            ["TiaMcp.ReleaseTool.Tests.CheckAllTests"] = new[] {
                "SchedulerHonorsDependenciesResourcesAndExclusiveChecks"
            },
            ["TiaMcp.ReleaseTool.Tests.ReleaseValidationTests"] = new[] {
                "ParallelPipelinesOverlapAndWriteSeparateEvidence",
                "ParallelPipelinesHonorSerialAndBoundedLimits",
                "ParallelPipelineFailureStopsQueuedJobsAndWaitsForRunningJobs",
                "ParallelPipelinesReportEveryFailureAndRetainBothLogs"
            },
            ["TiaMcp.ReleaseTool.Tests.CheckAllRound5Tests"] = new[] {
                "SensitiveExclusiveLaneOverlapsBothGeneralAndStability",
                "SensitiveWorkersHaveDisjointCpusAndDoNotSerializeEachOther",
                "JobDeadlineFailsByNameAndStopsOwnedParentAndOrphan",
                "EmptyJobAccountingStillWaitsForObservedProcessHandles",
                "PythonCheckHasAnOuterJobDeadlineAndItsOwnedProcessExits",
                "SystemProcessWithoutAWorktreeArgumentIsOwnedByItsJob",
                "SensitiveCpuCapPreservesTheRunnersIoWorkers",
                "ParallelMsbuildNodesKeepWorktreeOwnershipAndDrainAfterTheirParentExits",
                "MemberExitingBetweenLookupAndObservationIsAlreadyGone"
            },
        };

        public static bool Contains(string className, string method) =>
            Methods.TryGetValue(className, out var methods) && Array.IndexOf(methods, method) >= 0;

        public static bool ContainsCost(string key)
        {
            int start = key.StartsWith("T:", StringComparison.Ordinal) || key.StartsWith("M:", StringComparison.Ordinal) ? 2 : 0;
            int end = key.IndexOf('(');
            string name = end < 0 ? key.Substring(start) : key.Substring(start, end - start);
            int dot = name.LastIndexOf('.');
            return dot >= 0 && Contains(name.Substring(0, dot), name.Substring(dot + 1));
        }

        public static int SensitivePartition(string key) =>
            key.StartsWith("T:PortedPortalAdmissionTests.Port_dispatch_takes_the_shared_worker_lane_before_journaling", StringComparison.Ordinal)
            || key.StartsWith("M:PortedPortalAdmissionTests.Port_dispatch_takes_the_shared_worker_lane_before_journaling", StringComparison.Ordinal) ? 1 : 0;

        public static int ReservedShardCount(string project) =>
            Path.GetFileNameWithoutExtension(project) == "TiaMcp.FoundationHost.Tests" ? 2 : 1;

        public static bool ReserveShard(string project) => Path.GetFileNameWithoutExtension(project) is
            "TiaMcp.FoundationHost.Tests" or "TiaMcp.EngineHost.Tests" or "TiaMcp.Engine.Tests" or "TiaMcp.KeptCode.Tests" or
            "TiaMcp.WorkerChannel.Tests" or "TiaMcp.WorkbenchControl.Tests" or "TiaOpenness.Core.Tests" or
            "TiaOpenness.Gui.Tests" or "TiaMcp.ShippedTools.Tests" or "TiaMcp.Updater.Tests" or
            "TiaMcp.SourceContracts.Tests" or "TiaMcp.ReleaseTool.Tests" or "TiaMcp.Engine.PromptRegistration.Tests";
    }
}
