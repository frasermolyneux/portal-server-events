resource "azurerm_api_management_api" "cod4x_ingest" {
  count = local.cod4x_ingest_enabled ? 1 : 0

  name                = "cod4x-ingest-v1"
  resource_group_name = local.api_management.resource_group_name
  api_management_name = local.api_management.name
  revision            = "1"
  display_name        = "CoD4x Ingest"
  path                = "ingest"
  protocols           = ["https"]

  subscription_required = true
}

# Membership of the CoD4x Plugin product (defined in portal-environments). The
# product carries the subscription key the plugin authenticates with; scoping the
# product to only this API keeps the key least-privilege.
resource "azurerm_api_management_product_api" "cod4x_ingest" {
  count = local.cod4x_ingest_enabled ? 1 : 0

  api_name            = azurerm_api_management_api.cod4x_ingest[0].name
  product_id          = local.cod4x_plugin_api.product_id
  api_management_name = local.api_management.name
  resource_group_name = local.api_management.resource_group_name
}

locals {
  cod4x_ingest_event_operations = {
    player_connected = {
      operation_id = "post-events-player-connected"
      display_name = "Post Player Connected Events"
      queue_name   = "player-connected"
    }
    player_disconnected = {
      operation_id = "post-events-player-disconnected"
      display_name = "Post Player Disconnected Events"
      queue_name   = "player-disconnected"
    }
    chat_message = {
      operation_id = "post-events-chat-message"
      display_name = "Post Chat Message Events"
      queue_name   = "chat-message"
    }
    server_connected = {
      operation_id = "post-events-server-connected"
      display_name = "Post Server Connected Events"
      queue_name   = "server-connected"
    }
    map_change = {
      operation_id = "post-events-map-change"
      display_name = "Post Map Change Events"
      queue_name   = "map-change"
    }
    server_status = {
      operation_id = "post-events-server-status"
      display_name = "Post Server Status Events"
      queue_name   = "server-status"
    }
    playtime_observation = {
      operation_id = "post-events-playtime-observation"
      display_name = "Post Playtime Observation Events"
      queue_name   = "playtime-observation"
    }
    ban_file_changed = {
      operation_id = "post-events-ban-file-changed"
      display_name = "Post Ban File Changed Events"
      queue_name   = "ban-file-changed"
    }
    player_ip_resolved = {
      operation_id = "post-events-player-ip-resolved"
      display_name = "Post Player IP Resolved Events"
      queue_name   = "player-ip-resolved"
    }
  }
}

resource "azurerm_api_management_api_operation" "cod4x_ingest_events_post" {
  for_each = local.cod4x_ingest_enabled ? local.cod4x_ingest_event_operations : {}

  operation_id        = each.value.operation_id
  api_name            = azurerm_api_management_api.cod4x_ingest[0].name
  api_management_name = local.api_management.name
  resource_group_name = local.api_management.resource_group_name
  display_name        = each.value.display_name
  method              = "POST"
  url_template        = "/events/${each.value.queue_name}"

  request {
    description = "Array of server event envelopes of the same event type."

    representation {
      content_type = "application/json"
    }
  }

  response {
    status_code = 202
    description = "Accepted"
  }

  response {
    status_code = 400
    description = "Bad Request"
  }

  response {
    status_code = 401
    description = "Unauthorized"
  }

  response {
    status_code = 502
    description = "Service Bus forwarding failed"
  }
}

resource "azurerm_api_management_api_operation_policy" "cod4x_ingest_events_post" {
  for_each = local.cod4x_ingest_enabled ? local.cod4x_ingest_event_operations : {}

  api_name            = azurerm_api_management_api.cod4x_ingest[0].name
  api_management_name = local.api_management.name
  resource_group_name = local.api_management.resource_group_name
  operation_id        = azurerm_api_management_api_operation.cod4x_ingest_events_post[each.key].operation_id

  xml_content = <<XML
<policies>
  <inbound>
    <base />
    <set-variable name="requestBodyJson" value="@(context.Request.Body.As&lt;string&gt;(true))" />

    <choose>
      <when condition="@{
        if (&quot;${each.value.queue_name}&quot; != &quot;playtime-observation&quot;)
        {
          return false;
        }

        var requestBody = (string)context.Variables[&quot;requestBodyJson&quot;];
        Newtonsoft.Json.Linq.JArray payload;
        try
        {
          payload = Newtonsoft.Json.Linq.JArray.Parse(requestBody);
        }
        catch (System.Exception)
        {
          return true;
        }
        foreach (var item in payload)
        {
          if (item == null || item.Type != Newtonsoft.Json.Linq.JTokenType.Object)
          {
            return true;
          }

          var playtime = item[&quot;playtime&quot;] ?? item[&quot;Playtime&quot;];
          if (playtime == null || playtime.Type != Newtonsoft.Json.Linq.JTokenType.Object)
          {
            return true;
          }

          var observationIdToken = playtime[&quot;observationId&quot;] ?? playtime[&quot;ObservationId&quot;];
          if (observationIdToken == null || observationIdToken.Type != Newtonsoft.Json.Linq.JTokenType.String)
          {
            return true;
          }

          System.Guid observationId;
          if (!System.Guid.TryParseExact((string)observationIdToken, &quot;D&quot;, out observationId))
          {
            return true;
          }
          if (observationId == System.Guid.Empty)
          {
            return true;
          }
        }

        return false;
      }">
        <return-response>
          <set-status code="400" reason="Invalid playtime observation" />
          <set-body>Expected an array of playtime events with non-empty playtime.observationId GUIDs.</set-body>
        </return-response>
      </when>
    </choose>

    <authentication-managed-identity resource="https://servicebus.azure.net/"
                                     client-id="${local.managed_identities.api_management.client_id}"
                                     output-token-variable-name="serviceBusToken" />

    <send-request mode="new" response-variable-name="serviceBusResponse" timeout="20" ignore-error="false">
      <set-url>@("https://${local.servicebus.fqdn}/${each.value.queue_name}/messages")</set-url>
      <set-method>POST</set-method>
      <set-header name="Authorization" exists-action="override">
        <value>@("Bearer " + (string)context.Variables[&quot;serviceBusToken&quot;])</value>
      </set-header>
      <set-header name="Content-Type" exists-action="override">
        <value>application/vnd.microsoft.servicebus.json</value>
      </set-header>
      <set-body>@{
        var payload = Newtonsoft.Json.Linq.JArray.Parse((string)context.Variables[&quot;requestBodyJson&quot;]);
        var batch = new Newtonsoft.Json.Linq.JArray();

        foreach (var item in payload)
        {
          string messageId;
          if (&quot;${each.value.queue_name}&quot; == &quot;playtime-observation&quot;)
          {
            var playtime = item[&quot;playtime&quot;] ?? item[&quot;Playtime&quot;];
            var observationIdToken = playtime[&quot;observationId&quot;] ?? playtime[&quot;ObservationId&quot;];
            messageId = System.Guid.Parse((string)observationIdToken).ToString(&quot;D&quot;);
          }
          else
          {
            var messageIdToken = item[&quot;messageId&quot;] ?? item[&quot;MessageId&quot;];
            messageId = messageIdToken == null ? System.Guid.NewGuid().ToString(&quot;D&quot;) : (string)messageIdToken;
          }

          batch.Add(new Newtonsoft.Json.Linq.JObject(
            new Newtonsoft.Json.Linq.JProperty(&quot;Body&quot;, item.ToString()),
            new Newtonsoft.Json.Linq.JProperty(&quot;BrokerProperties&quot;, new Newtonsoft.Json.Linq.JObject(new Newtonsoft.Json.Linq.JProperty(&quot;MessageId&quot;, messageId)))
          ));
        }

        return batch.ToString();
      }</set-body>
    </send-request>

    <choose>
      <when condition="@(((IResponse)context.Variables[&quot;serviceBusResponse&quot;]).StatusCode &gt;= 200 &amp;&amp; ((IResponse)context.Variables[&quot;serviceBusResponse&quot;]).StatusCode &lt; 300)">
        <return-response>
          <set-status code="202" reason="Accepted" />
        </return-response>
      </when>
      <otherwise>
        <return-response>
          <set-status code="502" reason="Bad Gateway" />
        </return-response>
      </otherwise>
    </choose>
  </inbound>
  <backend>
    <base />
  </backend>
  <outbound>
    <base />
  </outbound>
  <on-error>
    <base />
  </on-error>
</policies>
XML
}

# Ban reads for the plugin. The CoD4x HTTP stack cannot carry a bearer JWT, so the
# plugin reads active bans through this proxy operation instead of calling the
# Repository API directly. APIM authenticates to the Repository API with its managed
# identity (which already holds the ServiceAccount app role), keeping the plugin on
# the short subscription-key header only.
resource "azurerm_api_management_api_operation" "cod4x_ingest_active_bans_get" {
  count = local.cod4x_ingest_enabled ? 1 : 0

  operation_id        = "get-active-bans"
  api_name            = azurerm_api_management_api.cod4x_ingest[0].name
  api_management_name = local.api_management.name
  resource_group_name = local.api_management.resource_group_name
  display_name        = "Get Active Bans"
  method              = "GET"
  url_template        = "/active-bans"

  request {
    description = "Returns active bans for the requested game type, proxied to the Repository API."

    query_parameter {
      name     = "gameType"
      required = true
      type     = "string"
    }

    query_parameter {
      name     = "skipEntries"
      required = false
      type     = "integer"
    }

    query_parameter {
      name     = "takeEntries"
      required = false
      type     = "integer"
    }
  }

  response {
    status_code = 200
    description = "OK"
  }

  response {
    status_code = 502
    description = "Repository API request failed"
  }
}

resource "azurerm_api_management_api_operation_policy" "cod4x_ingest_active_bans_get" {
  count = local.cod4x_ingest_enabled ? 1 : 0

  api_name            = azurerm_api_management_api.cod4x_ingest[0].name
  api_management_name = local.api_management.name
  resource_group_name = local.api_management.resource_group_name
  operation_id        = azurerm_api_management_api_operation.cod4x_ingest_active_bans_get[0].operation_id

  xml_content = <<XML
<policies>
  <inbound>
    <base />
    <authentication-managed-identity resource="${local.repository_api.application.primary_identifier_uri}"
                                     client-id="${local.managed_identities.api_management.client_id}"
                                     output-token-variable-name="repositoryToken" />
    <set-header name="Authorization" exists-action="override">
      <value>@("Bearer " + (string)context.Variables[&quot;repositoryToken&quot;])</value>
    </set-header>
    <set-backend-service base-url="${local.repository_api.api_management.endpoint}" />
    <rewrite-uri template="/v1/admin-actions?filter=ActiveBans&amp;order=CreatedDesc" copy-unmatched-params="true" />
  </inbound>
  <backend>
    <base />
  </backend>
  <outbound>
    <base />
  </outbound>
  <on-error>
    <base />
  </on-error>
</policies>
XML
}

# Universal rules transport for the CoD4x plugin. APIM authenticates to the
# Server Events Function with managed identity.
locals {
  cod4x_ingest_rules_operations = {
    evaluate = {
      display_name = "Evaluate Rules"
      description  = "Evaluate a normalized rules trigger."
    }
    outcomes = {
      display_name = "Record Rules Action Outcome"
      description  = "Record a rules action outcome."
    }
    poll = {
      display_name = "Poll Rules Execution"
      description  = "Poll pending rules executions."
    }
  }
}

resource "azurerm_api_management_api_operation" "cod4x_ingest_rules_post" {
  for_each = local.cod4x_ingest_enabled ? local.cod4x_ingest_rules_operations : {}

  operation_id        = "post-rules-${each.key}"
  api_name            = azurerm_api_management_api.cod4x_ingest[0].name
  api_management_name = local.api_management.name
  resource_group_name = local.api_management.resource_group_name
  display_name        = each.value.display_name
  method              = "POST"
  url_template        = "/rules/${each.key}"

  request {
    description = each.value.description

    representation {
      content_type = "application/json"
    }
  }

  response {
    status_code = 200
    description = "Rules request completed"
  }

  response {
    status_code = 400
    description = "Invalid request"
  }

  response {
    status_code = 500
    description = "Rules request failed"
  }
}

resource "azurerm_api_management_api_operation_policy" "cod4x_ingest_rules_post" {
  for_each = local.cod4x_ingest_enabled ? local.cod4x_ingest_rules_operations : {}

  api_name            = azurerm_api_management_api.cod4x_ingest[0].name
  api_management_name = local.api_management.name
  resource_group_name = local.api_management.resource_group_name
  operation_id        = azurerm_api_management_api_operation.cod4x_ingest_rules_post[each.key].operation_id

  xml_content = <<XML
<policies>
  <inbound>
    <base />
    <authentication-managed-identity resource="${local.server_events_api.application.primary_identifier_uri}"
                                     client-id="${local.managed_identities.api_management.client_id}"
                                     output-token-variable-name="serverEventsToken" />
    <set-header name="Authorization" exists-action="override">
      <value>@("Bearer " + (string)context.Variables[&quot;serverEventsToken&quot;])</value>
    </set-header>
    <set-backend-service base-url="https://${azurerm_linux_function_app.function_app.default_hostname}" />
    <rewrite-uri template="/api/rules/${each.key}" copy-unmatched-params="false" />
  </inbound>
  <backend>
    <forward-request timeout="15" />
  </backend>
  <outbound>
    <base />
  </outbound>
  <on-error>
    <base />
  </on-error>
</policies>
XML
}

