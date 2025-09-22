using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Minimaps.Services;

public class BackendClient
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public BackendClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<T> GetAsync<T>(string endpoint, CancellationToken cancellation = default)
    {
        var response = await _httpClient.GetAsync(endpoint, cancellation);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, _jsonOptions);
    }

    public async Task<T> PostAsync<T>(string endpoint, object data, CancellationToken cancellation = default)
    {
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(endpoint, content, cancellation);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync();

        return JsonSerializer.Deserialize<T>(responseJson, _jsonOptions);
    }

    public async Task PutAsync(string endpoint, Stream imageData, string contentType, string? expectedHash = null, CancellationToken cancellation = default)
    {
        using var content = new StreamContent(imageData);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        if (!string.IsNullOrEmpty(expectedHash))
            content.Headers.Add("X-Expected-Hash", expectedHash);

        var response = await _httpClient.PutAsync(endpoint, content, cancellation);
        response.EnsureSuccessStatusCode();
    }
}