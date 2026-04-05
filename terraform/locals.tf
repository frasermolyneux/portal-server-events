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

  managed_identities     = data.terraform_remote_state.portal_environments.outputs.managed_identities
  server_events_identity = local.managed_identities["server_events"]

  app_insights     = data.terraform_remote_state.portal_core.outputs.app_insights
  app_service_plan = data.terraform_remote_state.portal_core.outputs.app_service_plans["apps"]
  servicebus       = data.terraform_remote_state.portal_core.outputs.servicebus_namespace

  function_app_name         = "fn-portal-server-events-${var.environment}-${var.location}-${random_id.environment_id.hex}"
  function_app_storage_name = "safn${random_id.environment_id.hex}"
}
