resource "azurerm_linux_function_app" "function_app" {
  name = local.function_app_name
  tags = var.tags

  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  service_plan_id = data.azurerm_service_plan.sp.id

  storage_account_name          = azurerm_storage_account.function_app_storage.name
  storage_uses_managed_identity = true

  https_only = true

  functions_extension_version = "~4"

  identity {
    type         = "UserAssigned"
    identity_ids = [local.server_events_identity.id]
  }

  auth_settings_v2 {
    auth_enabled    = true
    runtime_version = "~1"

    require_authentication = true
    unauthenticated_action = "Return401"
    excluded_paths = [
      "/api/health/live",
      "/api/health/ready",
      "/api/v1/ReprocessDeadLetterQueue"
    ]
    require_https         = true
    http_route_api_prefix = "/api"

    login {
      token_store_enabled = false
    }

    active_directory_v2 {
      client_id            = local.server_events_api.application.client_id
      tenant_auth_endpoint = "https://login.microsoftonline.com/${data.azurerm_client_config.current.tenant_id}/v2.0"
      allowed_audiences    = [local.server_events_api.application.primary_identifier_uri]
    }
  }

  key_vault_reference_identity_id = local.server_events_identity.id

  site_config {
    application_stack {
      use_dotnet_isolated_runtime = true
      dotnet_version              = "10.0"
    }

    application_insights_connection_string = data.azurerm_application_insights.app_insights.connection_string
    application_insights_key               = data.azurerm_application_insights.app_insights.instrumentation_key

    ftps_state          = "Disabled"
    always_on           = true
    minimum_tls_version = "1.2"

    health_check_path                 = "/api/health/live"
    health_check_eviction_time_in_min = 5
  }

  app_settings = {
    "AzureAppConfiguration__Endpoint"                = local.app_configuration_endpoint
    "AzureAppConfiguration__ManagedIdentityClientId" = local.server_events_identity.client_id
    "AzureAppConfiguration__Environment"             = var.environment

    "AZURE_CLIENT_ID" = local.server_events_identity.client_id

    "ApplicationInsightsAgent_EXTENSION_VERSION" = "~3"

    "ServiceBusConnection__fullyQualifiedNamespace" = local.servicebus.fqdn
    "ServiceBusConnection__ManagedIdentityClientId" = local.server_events_identity.client_id

    // https://learn.microsoft.com/en-us/azure/azure-monitor/profiler/profiler-azure-functions#app-settings-for-enabling-profiler
    "APPINSIGHTS_PROFILERFEATURE_VERSION"  = "1.0.0"
    "DiagnosticServices_EXTENSION_VERSION" = "~3"
  }

  lifecycle {
    ignore_changes = [
      app_settings["WEBSITE_RUN_FROM_PACKAGE"] # Ignore changes to this property as it will be updated by the deployment pipeline
    ]
  }
}
