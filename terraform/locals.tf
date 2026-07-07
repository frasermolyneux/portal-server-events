locals {
  workload_resource_groups = {
    for location in [var.location] :
    location => data.terraform_remote_state.platform_workloads.outputs.workload_resource_groups[var.workload_name][var.environment].resource_groups[lower(location)]
  }

  workload_resource_group = local.workload_resource_groups[var.location]

  action_group_map = {
    critical      = data.terraform_remote_state.platform_monitoring.outputs.monitor_action_groups.critical
    high          = data.terraform_remote_state.platform_monitoring.outputs.monitor_action_groups.high
    moderate      = data.terraform_remote_state.platform_monitoring.outputs.monitor_action_groups.moderate
    low           = data.terraform_remote_state.platform_monitoring.outputs.monitor_action_groups.low
    informational = data.terraform_remote_state.platform_monitoring.outputs.monitor_action_groups.informational
  }

  app_configuration_endpoint = data.terraform_remote_state.portal_environments.outputs.app_configuration.endpoint
  api_management             = data.terraform_remote_state.portal_environments.outputs.api_management
  server_events_api          = try(data.terraform_remote_state.portal_environments.outputs.server_events_api, null)
  repository_api             = data.terraform_remote_state.portal_environments.outputs.repository_api
  cod4x_plugin_api           = data.terraform_remote_state.portal_environments.outputs.cod4x_plugin_api
  cod4x_ingest_enabled       = local.server_events_api != null

  managed_identities     = data.terraform_remote_state.portal_environments.outputs.managed_identities
  server_events_identity = local.managed_identities["server_events"]

  app_insights     = data.terraform_remote_state.portal_core.outputs.app_insights
  app_service_plan = data.terraform_remote_state.portal_core.outputs.app_service_plans["apps"]
  servicebus       = data.terraform_remote_state.portal_core.outputs.servicebus_namespace

  function_app_name         = "fn-portal-server-events-${var.environment}-${var.location}-${random_id.environment_id.hex}"
  function_app_storage_name = "safn${random_id.environment_id.hex}"
}
