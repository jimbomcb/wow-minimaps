using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Minimaps.Shared;

/// <summary>
/// event log that supplies batched (2000 char) events to a webhook
/// </summary>
public class WebhookEventLog(string? webhookUrl, ILogger? logger = null) : IDisposable
{
    private readonly bool _enabled = !string.IsNullOrEmpty(webhookUrl);
    private readonly string? _webhookUrl = webhookUrl;
    private readonly HttpClient _httpClient = new();
    private readonly List<string> _messageQueue = [];
    private readonly object _queueLock = new();
    private readonly ILogger? _logger = logger;
    private volatile bool _disposed;
    private DateTime _rateLimitResetTime = DateTime.MinValue;

    public void Post(string message, bool log = true)
    {
        if (_disposed || !_enabled) 
            return;

        if (log)
            _logger?.LogInformation(message);

        lock (_queueLock)
        {
            _messageQueue.Add($"{DateTime.UtcNow:O} - {message}");
        }
    }

    /// <summary>
    /// Send all queued messages, batching them into 2000 character chunks
    /// </summary>
    public async Task SendQueuedAsync()
    {
        if (_disposed || !_enabled) 
            return;

        // check rate limit before processing
        if (DateTime.UtcNow < _rateLimitResetTime)
            return;

        var messagesToProcess = new List<string>();
        lock (_queueLock)
        {
            if (_messageQueue.Count == 0)
                return;

            messagesToProcess.AddRange(_messageQueue);
            _messageQueue.Clear();
        }

        await ProcessMessages(messagesToProcess);
    }

    private async Task ProcessMessages(List<string> messages)
    {
        var currentBatch = new StringBuilder();
        var processedIndex = 0;

        try
        {
            for (int i = 0; i < messages.Count; i++)
            {
                var message = messages[i];

                // if adding this message would exceed 2000 chars, send current batch first
                if (currentBatch.Length > 0 && currentBatch.Length + message.Length + 1 > 2000)
                {
                    await SendWebhook(currentBatch.ToString());
                    currentBatch.Clear();
                    
                    if (DateTime.UtcNow < _rateLimitResetTime)
                        break;
                }

                if (currentBatch.Length > 0)
                    currentBatch.AppendLine();

                currentBatch.Append(message);
                processedIndex = i + 1;
            }

            if (currentBatch.Length > 0 && processedIndex == messages.Count)
                await SendWebhook(currentBatch.ToString());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing messages");
        }

        // return any unprocessed messages to the log (at front)
        if (processedIndex < messages.Count)
        {
            lock (_queueLock)
            {
                for (int i = processedIndex; i < messages.Count; i++)
                {
                    _messageQueue.Insert(i - processedIndex, messages[i]);
                }
            }
        }
    }

    private async Task SendWebhook(string content)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(_webhookUrl, new { content });
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (response.Headers.RetryAfter?.Delta is TimeSpan retryAfter)
                {
                    _rateLimitResetTime = DateTime.UtcNow.Add(retryAfter);
                    _logger?.LogWarning("Rate limited, will retry after {RetryAfter} seconds", retryAfter.TotalSeconds);
                }
            }
            else if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger?.LogError("Webhook failed: {StatusCode} - {Error}", response.StatusCode, error);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error sending to webhook");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        lock (_queueLock)
        {
            if (_messageQueue.Count > 0)
                _logger?.LogError("Disposing WebhookEventLog with {Count} unsent messages", _messageQueue.Count);
        }
        
        _httpClient?.Dispose();
    }
}
