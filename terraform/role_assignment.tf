resource "azurerm_role_assignment" "app_to_storage" {
  scope                = azurerm_storage_account.function_app_storage.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = local.server_events_identity.principal_id
}
