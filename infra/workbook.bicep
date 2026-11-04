// Azure Monitor workbook for Live Coach: reply latency, reply decisions, token use and reliability counters.
// Content-free: the metrics carry only fixed-vocabulary tags, and the decision tables read the one "ReplySummary"
// console line the API writes per reply. Deployable on its own or as a module of main.bicep.
//
// Histograms reach customMetrics as one pre-aggregated row per minute and tag set (valueSum, valueCount, valueMin,
// valueMax); the Azure Monitor exporter sends no buckets, so true percentiles cannot be computed from metrics. The
// metric tiles show count-weighted percentiles of each row's mean (exact when a row holds one reply, as is usual in a
// live meeting) with exact avg and max. The log tiles give true per-reply first-line percentiles.

@description('Display name of the workbook in the Azure portal.')
param workbookDisplayName string = 'Live Coach telemetry'
@description('Azure region of the workbook resource.')
param location string = resourceGroup().location
@description('Resource ID of the Application Insights component that receives the API metrics (customMetrics).')
param appInsightsId string
@description('Resource ID of the Log Analytics workspace that receives the container console logs.')
param logAnalyticsWorkspaceId string

// Container Apps writes console logs to ContainerAppConsoleLogs (Azure Monitor destination, column Log) or to
// ContainerAppConsoleLogs_CL (Log Analytics destination, column Log_s); both are read. With the default plain console
// format the message is a log line of its own; with a JSON console format it sits inside the Message field, so the
// key=value pairs are read only up to the first double quote.
var replySummaries = '''
// One content-free "ReplySummary key=value ..." console line per reply; plain or JSON console format.
union isfuzzy=true ContainerAppConsoleLogs, ContainerAppConsoleLogs_CL
| extend line = tostring(coalesce(column_ifexists("Log", ""), column_ifexists("Log_s", "")))
| where line has "ReplySummary"
| extend pairs = extract(@'ReplySummary\s+([^"]*)', 1, line)
| extend outcome = extract(@'\boutcome=(\S+)', 1, pairs), start_reason = extract(@'\bstart_reason=(\S+)', 1, pairs),
    classifier = extract(@'\bclassifier=(\S+)', 1, pairs), decision = extract(@'\bdecision=(\S+)', 1, pairs),
    waits = toint(extract(@'\bwaits=(\S+)', 1, pairs)), quiet_armed = extract(@'\bquiet_armed=(\S+)', 1, pairs),
    queued = extract(@'\bqueued=(\S+)', 1, pairs), queue_wait_ms = toreal(extract(@'\bqueue_wait_ms=(\S+)', 1, pairs)),
    speculation = extract(@'\bspeculation=(\S+)', 1, pairs), spec_edit = extract(@'\bspec_edit=(\S+)', 1, pairs),
    speech_restarts = toint(extract(@'\bspeech_restarts=(\S+)', 1, pairs)),
    first_line_ms = toreal(extract(@'\bfirst_line_ms=(\S+)', 1, pairs))
'''

var content = {
  version: 'Notebook/1.0'
  items: [
    {
      type: 1
      name: 'intro'
      content: {
        json: '## Live Coach telemetry\nContent-free: metrics carry only fixed-vocabulary tags, and the decision tables read the one `ReplySummary` console line each reply writes. Times are in milliseconds from the reply\'s final transcript unless the metric says otherwise. Metrics are exported once a minute; a tile stays empty until the deployed API emits its metric.'
      }
    }
    {
      type: 9
      name: 'parameters'
      content: {
        version: 'KqlParameterItem/1.0'
        parameters: [
          {
            id: 'time-range'
            version: 'KqlParameterItem/1.0'
            name: 'TimeRange'
            label: 'Time range'
            type: 4
            isRequired: true
            value: { durationMs: 86400000 }
            typeSettings: {
              selectableValues: [
                { durationMs: 3600000 }
                { durationMs: 14400000 }
                { durationMs: 43200000 }
                { durationMs: 86400000 }
                { durationMs: 259200000 }
                { durationMs: 604800000 }
                { durationMs: 2592000000 }
              ]
              allowCustom: true
            }
          }
        ]
        style: 'pills'
        queryType: 0
        resourceType: 'microsoft.insights/components'
      }
    }
    {
      type: 1
      name: 'latency-header'
      content: { json: '### Latency' }
    }
    {
      type: 3
      name: 'first-line'
      customWidth: '50'
      content: {
        version: 'KqlItem/1.0'
        title: 'First line ready (final transcript to completed primary line)'
        query: '''
// No histogram buckets reach customMetrics: p50/p95 are count-weighted percentiles of per-minute means
// (exact when a minute holds one reply); max is exact.
customMetrics
| where name == "voiceassistant.reply.final_to_primary_done" and valueCount > 0
| summarize p50_ms = percentilew(valueSum / valueCount, valueCount, 50), p95_ms = percentilew(valueSum / valueCount, valueCount, 95),
    max_ms = max(valueMax) by bin(timestamp, {TimeRange:grain})
| order by timestamp asc
'''
        size: 0
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
        visualization: 'timechart'
      }
    }
    {
      type: 3
      name: 'recognition-lag'
      customWidth: '50'
      content: {
        version: 'KqlItem/1.0'
        title: 'Speech recognition lag p95, by result kind'
        query: '''
// No histogram buckets reach customMetrics: p95 is a count-weighted percentile of per-minute means.
customMetrics
| where name == "voiceassistant.speech.recognition_lag" and valueCount > 0
| summarize p95_ms = percentilew(valueSum / valueCount, valueCount, 95) by result_kind = tostring(customDimensions["kind"]), bin(timestamp, {TimeRange:grain})
| order by timestamp asc
'''
        size: 0
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
        visualization: 'timechart'
      }
    }
    {
      type: 3
      name: 'timings'
      content: {
        version: 'KqlItem/1.0'
        title: 'Reply and retrieval timings'
        query: '''
// No histogram buckets reach customMetrics: p50/p95 are count-weighted percentiles of per-minute means
// (exact when a minute holds one sample); samples, avg and max are exact.
let timings = dynamic(["voiceassistant.reply.final_to_primary_done", "voiceassistant.stt_final_to_first_delta",
    "voiceassistant.stt_final_to_completed", "voiceassistant.reply.settle_wait", "voiceassistant.reply.queue_wait",
    "voiceassistant.reply.retrieval_wait", "voiceassistant.reply.first_token", "voiceassistant.reply.classifier_wait",
    "voiceassistant.reply.korean_ready", "voiceassistant.retrieval_duration", "voiceassistant.reply.speculative_head_start",
    "voiceassistant.speech.recognition_lag", "voiceassistant.reply.visible"]);
customMetrics
| where name in (timings) and valueCount > 0
| extend split = coalesce(tostring(customDimensions["kind"]), tostring(customDimensions.trigger), tostring(customDimensions.outcome))
| summarize samples = sum(valueCount), avg_ms = round(sum(valueSum) / sum(valueCount)),
    p50_ms = round(percentilew(valueSum / valueCount, valueCount, 50)), p95_ms = round(percentilew(valueSum / valueCount, valueCount, 95)),
    max_ms = round(max(valueMax)) by metric = name, split
| order by metric asc, split asc
'''
        size: 0
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
        visualization: 'table'
      }
    }
    {
      type: 1
      name: 'decisions-header'
      content: { json: '### Reply decisions' }
    }
    {
      type: 3
      name: 'outcomes'
      customWidth: '50'
      content: {
        version: 'KqlItem/1.0'
        title: 'Reply outcomes by start reason and classifier'
        query: '''
customMetrics
| where name == "voiceassistant.reply.outcomes"
| summarize replies = sum(valueSum) by start_reason = tostring(customDimensions.start_reason),
    classifier = tostring(customDimensions.classifier), outcome = tostring(customDimensions.outcome)
| order by replies desc
'''
        size: 0
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
        visualization: 'table'
      }
    }
    {
      type: 3
      name: 'decision'
      customWidth: '25'
      content: {
        version: 'KqlItem/1.0'
        title: 'Respond or listen'
        query: '''
customMetrics
| where name == "voiceassistant.reply.decision"
| summarize turns = sum(valueSum) by decision = tostring(customDimensions.decision)
'''
        size: 0
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
        visualization: 'piechart'
      }
    }
    {
      type: 3
      name: 'feedback'
      customWidth: '25'
      content: {
        version: 'KqlItem/1.0'
        title: 'Reply feedback'
        query: '''
customMetrics
| where name == "voiceassistant.reply.feedback"
| summarize reports = sum(valueSum) by reason = tostring(customDimensions.reason)
| order by reports desc
'''
        size: 0
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
        visualization: 'barchart'
      }
    }
    {
      type: 3
      name: 'decision-latency'
      content: {
        version: 'KqlItem/1.0'
        title: 'First line by start reason and decision (true per-reply percentiles)'
        query: '${replySummaries}| summarize replies = count(), p50_first_line_ms = percentile(first_line_ms, 50), p95_first_line_ms = percentile(first_line_ms, 95),\n    avg_queue_wait_ms = round(avg(queue_wait_ms)) by start_reason, decision, outcome\n| order by replies desc\n'
        size: 0
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.operationalinsights/workspaces'
        crossComponentResources: [logAnalyticsWorkspaceId]
        visualization: 'table'
      }
    }
    {
      type: 3
      name: 'reply-summaries'
      content: {
        version: 'KqlItem/1.0'
        title: 'Latest replies (ReplySummary)'
        query: '${replySummaries}| project TimeGenerated, outcome, start_reason, classifier, decision, first_line_ms, waits, quiet_armed, queued,\n    queue_wait_ms, speculation, spec_edit, speech_restarts\n| order by TimeGenerated desc\n| take 500\n'
        size: 0
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.operationalinsights/workspaces'
        crossComponentResources: [logAnalyticsWorkspaceId]
        visualization: 'table'
      }
    }
    {
      type: 1
      name: 'tokens-header'
      content: { json: '### Tokens and prompt cache' }
    }
    {
      type: 3
      name: 'tokens'
      content: {
        version: 'KqlItem/1.0'
        title: 'Tokens by operation; cached share of input'
        query: '''
// "cached" is the part of "input" served from the prompt cache, so the cached share is cached / input.
customMetrics
| where name == "voiceassistant.chat.tokens"
| extend operation = tostring(customDimensions.operation), token_type = tostring(customDimensions["type"])
| summarize input = sumif(valueSum, token_type == "input"), cached = sumif(valueSum, token_type == "cached"),
    output = sumif(valueSum, token_type == "output"), reasoning = sumif(valueSum, token_type == "reasoning") by operation
| extend cached_share_pct = iff(input > 0, round(100.0 * cached / input, 1), real(null))
| order by operation asc
'''
        size: 0
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
        visualization: 'table'
      }
    }
    {
      type: 1
      name: 'reliability-header'
      content: { json: '### Reliability' }
    }
    {
      type: 3
      name: 'active-sessions'
      customWidth: '50'
      content: {
        version: 'KqlItem/1.0'
        title: 'Open meeting sessions'
        query: '''
customMetrics
| where name == "voiceassistant.sessions.active"
| summarize sessions = max(valueMax) by bin(timestamp, {TimeRange:grain})
| order by timestamp asc
'''
        size: 0
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
        visualization: 'timechart'
      }
    }
    {
      type: 3
      name: 'counters'
      customWidth: '50'
      content: {
        version: 'KqlItem/1.0'
        title: 'Errors, retries, restarts and other counters'
        query: '''
let counters = dynamic(["voiceassistant.session.errors", "voiceassistant.retrieval.failures", "voiceassistant.chat.throttled",
    "voiceassistant.chat.retries", "voiceassistant.speech.restarts", "voiceassistant.warmup.failures", "voiceassistant.session.ended",
    "voiceassistant.reply.speculation", "voiceassistant.reply.prefetch", "voiceassistant.reply.grounding", "voiceassistant.session.resume"]);
customMetrics
| where name in (counters)
| extend detail = tostring(bag_remove_keys(customDimensions, dynamic(["provider", "trigger", "route", "response_mode"])))
| summarize total = sum(valueSum) by name, detail
| order by name asc, total desc
'''
        size: 0
        timeContextFromParameter: 'TimeRange'
        queryType: 0
        resourceType: 'microsoft.insights/components'
        visualization: 'table'
      }
    }
  ]
  fallbackResourceIds: [appInsightsId]
  '$schema': 'https://github.com/Microsoft/Application-Insights-Workbooks/blob/master/schema/workbook.json'
}

resource workbook 'Microsoft.Insights/workbooks@2023-06-01' = {
  name: guid(resourceGroup().id, appInsightsId, 'live-coach-workbook')
  location: location
  kind: 'shared'
  properties: {
    displayName: workbookDisplayName
    category: 'workbook'
    // The portal lists a component's workbooks by its lower-case resource ID.
    sourceId: toLower(appInsightsId)
    version: 'Notebook/1.0'
    serializedData: string(content)
  }
}

output workbookId string = workbook.id
