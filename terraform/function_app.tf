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

  key_vault_reference_identity_id = local.server_events_identity.id

  site_config {
    application_stack {
      use_dotnet_isolated_runtime = true
      dotnet_version              = "9.0"
    }

    application_insights_connection_string = data.azurerm_application_insights.app_insights.connection_string
    application_insights_key               = data.azurerm_application_insights.app_insights.instrumentation_key

    ftps_state          = "Disabled"
    always_on           = true
    minimum_tls_version = "1.2"

    health_check_path                 = "/api/health"
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
