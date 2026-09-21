using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using StudyHive.Api.Contracts;

namespace StudyHive.Api.Services;

/// <summary>Talks to the internal FastAPI Resource endpoint. Never called directly by React/Flutter — only from <see cref="WorkflowOrchestrationService"/>.</summary>
public interface IResourceClient
{
    Task<ResourceResponse> PrepareReservationAsync(ResourceRequest request, CancellationToken ct);
}

public sealed class ResourceClient(HttpClient http) : IResourceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<ResourceResponse> PrepareReservationAsync(ResourceRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/resource/prepare-reservation", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ResourceResponse>(JsonOptions, ct);
        return body ?? throw new InvalidOperationException("Resource service returned an empty response body.");
    }
}
