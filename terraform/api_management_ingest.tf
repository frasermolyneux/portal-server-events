resource "azurerm_api_management_named_value" "ingest_servicebus_namespace_fqdn" {
  count = local.cod4x_ingest_enabled ? 1 : 0

  name                = "cod4x-ingest-servicebus-namespace-fqdn"
  resource_group_name = local.api_management.resource_group_name
  api_management_name = local.api_management.name
  display_name        = "cod4x-ingest-servicebus-namespace-fqdn"
  value               = local.servicebus.fqdn
  secret              = false
}

resource "azurerm_api_management_api" "cod4x_ingest" {
  count = local.cod4x_ingest_enabled ? 1 : 0

  name                = "cod4x-ingest-v1"
  resource_group_name = local.api_management.resource_group_name
  api_management_name = local.api_management.name
  revision            = "1"
  display_name        = "CoD4x Ingest"
  path                = "ingest"
  protocols           = ["https"]

  subscription_required = false
}

resource "azurerm_api_management_api_operation" "cod4x_ingest_events_post" {
  count = local.cod4x_ingest_enabled ? 1 : 0

  operation_id        = "post-events-by-type"
  api_name            = azurerm_api_management_api.cod4x_ingest[0].name
  api_management_name = local.api_management.name
  resource_group_name = local.api_management.resource_group_name
  display_name        = "Post Events By Type"
  method              = "POST"
  url_template        = "/events/{eventType}"

  template_parameter {
    name        = "eventType"
    required    = true
    type        = "string"
    description = "Event type route segment used to map to a Service Bus queue."
  }

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
    status_code = 429
    description = "Too Many Requests"
  }

  response {
    status_code = 502
    description = "Service Bus forwarding failed"
  }
}

resource "azurerm_api_management_api_operation_policy" "cod4x_ingest_events_post" {
  count = local.cod4x_ingest_enabled ? 1 : 0

  api_name            = azurerm_api_management_api.cod4x_ingest[0].name
  api_management_name = local.api_management.name
  resource_group_name = local.api_management.resource_group_name
  operation_id        = azurerm_api_management_api_operation.cod4x_ingest_events_post[0].operation_id

  xml_content = <<XML
<policies>
  <inbound>
    <base />
    <validate-jwt header-name="Authorization"
                  failed-validation-httpcode="401"
                  failed-validation-error-message="Unauthorized">
      <openid-config url="https://login.microsoftonline.com/${var.portal_environments_state.tenant_id}/v2.0/.well-known/openid-configuration" />
      <audiences>
        <audience>${local.server_events_api.application.primary_identifier_uri}</audience>
      </audiences>
      <required-claims>
        <claim name="roles" match="any">
          <value>ServiceAccount</value>
        </claim>
      </required-claims>
    </validate-jwt>

    <set-variable name="requestBody" value="@(context.Request.Body.As&lt;JArray&gt;(preserveContent: true))" />

    <choose>
      <when condition="@(context.Variables.GetValueOrDefault&lt;JArray&gt;(&quot;requestBody&quot;) == null)">
        <return-response>
          <set-status code="400" reason="Bad Request" />
          <set-header name="Content-Type" exists-action="override">
            <value>application/json</value>
          </set-header>
          <set-body>{&quot;error&quot;:&quot;Request body must be a JSON array.&quot;}</set-body>
        </return-response>
      </when>
      <when condition="@(((JArray)context.Variables[&quot;requestBody&quot;]).Count == 0)">
        <return-response>
          <set-status code="400" reason="Bad Request" />
          <set-header name="Content-Type" exists-action="override">
            <value>application/json</value>
          </set-header>
          <set-body>{&quot;error&quot;:&quot;At least one event is required.&quot;}</set-body>
        </return-response>
      </when>
      <when condition="@(((JArray)context.Variables[&quot;requestBody&quot;]).Count &gt; 100)">
        <return-response>
          <set-status code="400" reason="Bad Request" />
          <set-header name="Content-Type" exists-action="override">
            <value>application/json</value>
          </set-header>
          <set-body>{&quot;error&quot;:&quot;Batch too large. Maximum is 100 events.&quot;}</set-body>
        </return-response>
      </when>
      <when condition="@(((JArray)context.Variables[&quot;requestBody&quot;]).Any(e =&gt; (e[&quot;eventGeneratedUtc&quot;] == null &amp;&amp; e[&quot;EventGeneratedUtc&quot;] == null) || (e[&quot;eventPublishedUtc&quot;] == null &amp;&amp; e[&quot;EventPublishedUtc&quot;] == null) || (e[&quot;serverId&quot;] == null &amp;&amp; e[&quot;ServerId&quot;] == null) || (e[&quot;gameType&quot;] == null &amp;&amp; e[&quot;GameType&quot;] == null) || (e[&quot;sequenceId&quot;] == null &amp;&amp; e[&quot;SequenceId&quot;] == null)))">
        <return-response>
          <set-status code="400" reason="Bad Request" />
          <set-header name="Content-Type" exists-action="override">
            <value>application/json</value>
          </set-header>
          <set-body>{&quot;error&quot;:&quot;One or more events are missing required fields.&quot;}</set-body>
        </return-response>
      </when>
    </choose>

    <set-variable name="eventType" value="@((string)context.Request.MatchedParameters[&quot;eventType&quot;])" />

    <choose>
      <when condition="@((string)context.Variables[&quot;eventType&quot;] == &quot;player-connected&quot;)">
        <set-variable name="queueName" value="player-connected" />
      </when>
      <when condition="@((string)context.Variables[&quot;eventType&quot;] == &quot;player-disconnected&quot;)">
        <set-variable name="queueName" value="player-disconnected" />
      </when>
      <when condition="@((string)context.Variables[&quot;eventType&quot;] == &quot;chat-message&quot;)">
        <set-variable name="queueName" value="chat-message" />
      </when>
      <when condition="@((string)context.Variables[&quot;eventType&quot;] == &quot;server-connected&quot;)">
        <set-variable name="queueName" value="server-connected" />
      </when>
      <when condition="@((string)context.Variables[&quot;eventType&quot;] == &quot;map-change&quot;)">
        <set-variable name="queueName" value="map-change" />
      </when>
      <when condition="@((string)context.Variables[&quot;eventType&quot;] == &quot;server-status&quot;)">
        <set-variable name="queueName" value="server-status" />
      </when>
      <when condition="@((string)context.Variables[&quot;eventType&quot;] == &quot;ban-file-changed&quot;)">
        <set-variable name="queueName" value="ban-file-changed" />
      </when>
      <when condition="@((string)context.Variables[&quot;eventType&quot;] == &quot;player-ip-resolved&quot;)">
        <set-variable name="queueName" value="player-ip-resolved" />
      </when>
      <otherwise>
        <return-response>
          <set-status code="400" reason="Bad Request" />
          <set-header name="Content-Type" exists-action="override">
            <value>application/json</value>
          </set-header>
          <set-body>{&quot;error&quot;:&quot;Unsupported event type.&quot;}</set-body>
        </return-response>
      </otherwise>
    </choose>

    <rate-limit-by-key calls="120" renewal-period="60" counter-key="@{
      var events = (JArray)context.Variables[&quot;requestBody&quot;];
      var serverId = (string)null;
      if (events.Count &gt; 0)
      {
        var first = (JObject)events[0];
        var serverIdToken = first[&quot;serverId&quot;] ?? first[&quot;ServerId&quot;];
        if (serverIdToken != null)
        {
          serverId = (string)serverIdToken;
        }
      }
      return string.IsNullOrWhiteSpace(serverId) ? (context.Request.IpAddress ?? &quot;unknown&quot;) : serverId;
    }" />

    <authentication-managed-identity resource="https://servicebus.azure.net"
                                     client-id="${local.managed_identities["api_management"].client_id}"
                                     output-token-variable-name="serviceBusToken" />

    <send-request mode="new" response-variable-name="serviceBusResponse" timeout="20" ignore-error="false">
      <set-url>@("https://{{${azurerm_api_management_named_value.ingest_servicebus_namespace_fqdn[0].name}}}/" + (string)context.Variables[&quot;queueName&quot;] + "/messages")</set-url>
      <set-method>POST</set-method>
      <set-header name="Authorization" exists-action="override">
        <value>@("Bearer " + (string)context.Variables[&quot;serviceBusToken&quot;])</value>
      </set-header>
      <set-header name="Content-Type" exists-action="override">
        <value>application/vnd.microsoft.servicebus.json</value>
      </set-header>
      <set-body>@{
        var payload = (JArray)context.Variables[&quot;requestBody&quot;];
        var batch = new JArray();

        foreach (var item in payload)
        {
          var messageIdToken = item[&quot;messageId&quot;] ?? item[&quot;MessageId&quot;];
          var messageId = messageIdToken == null ? Guid.NewGuid().ToString(&quot;D&quot;) : (string)messageIdToken;

          batch.Add(new JObject(
            new JProperty(&quot;Body&quot;, item.ToString(Newtonsoft.Json.Formatting.None)),
            new JProperty(&quot;BrokerProperties&quot;, new JObject(new JProperty(&quot;MessageId&quot;, messageId)))
          ));
        }

        return batch.ToString(Newtonsoft.Json.Formatting.None);
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
