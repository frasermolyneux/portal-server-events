resource "azurerm_monitor_metric_alert" "dead_letter_messages" {
  name                = "${var.workload_name}-${var.environment}-dead-letter-messages"
  resource_group_name = data.azurerm_resource_group.rg.name
  scopes              = [local.servicebus.id]
  description         = "Triggered when dead-lettered messages exceed threshold"
  severity            = 2
  frequency           = "PT5M"
  window_size         = "PT15M"

  criteria {
    metric_namespace = "Microsoft.ServiceBus/namespaces"
    metric_name      = "DeadletteredMessages"
    aggregation      = "Maximum"
    operator         = "GreaterThan"
    threshold        = 10
  }

  action {
    action_group_id = local.action_group_map.high.id
  }

  tags = var.tags
}

resource "azurerm_monitor_metric_alert" "queue_backlog" {
  name                = "${var.workload_name}-${var.environment}-queue-backlog"
  resource_group_name = data.azurerm_resource_group.rg.name
  scopes              = [local.servicebus.id]
  description         = "Triggered when active messages exceed threshold indicating queue backlog"
  severity            = 2
  frequency           = "PT5M"
  window_size         = "PT15M"

  criteria {
    metric_namespace = "Microsoft.ServiceBus/namespaces"
    metric_name      = "ActiveMessages"
    aggregation      = "Maximum"
    operator         = "GreaterThan"
    threshold        = 1000
  }

  action {
    action_group_id = local.action_group_map.high.id
  }

  tags = var.tags
}

resource "azurerm_monitor_scheduled_query_rules_alert_v2" "processor_failure_rate" {
  count = var.environment == "prd" ? 1 : 0

  name                = "${var.workload_name}-${var.environment}-processor-failure-rate"
  resource_group_name = data.azurerm_resource_group.rg.name
  location            = var.location
  description         = "Triggered when processor function failure rate exceeds threshold"
  severity            = 1

  evaluation_frequency = "PT5M"
  window_duration      = "PT5M"
  scopes               = [local.app_insights.id]

  criteria {
    query = <<-QUERY
      requests
      | where timestamp > ago(5m)
      | where name startswith "Process"
      | where success == false
      | summarize failureCount = count()
    QUERY

    time_aggregation_method = "Count"
    operator                = "GreaterThan"
    threshold               = 50

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [local.action_group_map.critical.id]
  }

  tags = var.tags
}
