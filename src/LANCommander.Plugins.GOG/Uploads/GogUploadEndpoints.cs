using LANCommander.Server.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace LANCommander.Plugins.GOG.Uploads;

/// <summary>
/// Receives installers administrators upload from the upload page.
/// </summary>
/// <remarks>
/// The body is read as it arrives and written straight to the upload's directory. Binding an
/// <see cref="IFormFile"/> instead would have ASP.NET buffer the whole file to a temporary file
/// first, and GOG installers run to tens of gigabytes.
/// </remarks>
public sealed class GogUploadEndpoints : IServerEndpointExtension
{
    public const string Prefix = "GOG";

    /// <summary>Where the files of an upload are posted.</summary>
    public static string UploadUrl(Guid uploadId) => $"/api/Plugins/{Prefix}/Uploads/{uploadId:N}";

    public string RoutePrefix => Prefix;

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Like the server's own upload endpoints, called by the upload control rather than a form
        endpoints.MapPost("/Uploads/{uploadId:guid}", ReceiveAsync).DisableAntiforgery();
    }

    internal static async Task<IResult> ReceiveAsync(
        Guid uploadId,
        HttpRequest request,
        [FromServices] GogUploadStore uploads,
        [FromServices] ILogger<GogUploadStore> logger,
        CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || HeaderUtilities.RemoveQuotes(contentType.Boundary).Value is not { Length: > 0 } boundary)
            return TypedResults.Text("Expected a file sent as multipart/form-data.", statusCode: StatusCodes.Status400BadRequest);

        var reader = new MultipartReader(boundary, request.Body);
        var saved = new List<GogUploadedFile>();

        try
        {
            while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
            {
                if (section.AsFileSection() is not { FileStream: { } stream } file)
                    continue;

                saved.Add(await uploads.SaveAsync(uploadId, file.FileName, stream, cancellationToken));
            }
        }
        catch (GogUploadException exception)
        {
            return TypedResults.Text(exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Could not store a file uploaded to {UploadId}", uploadId);

            return TypedResults.Text($"The file could not be stored: {exception.Message}", statusCode: StatusCodes.Status500InternalServerError);
        }

        if (saved.Count == 0)
            return TypedResults.Text("The request carried no file.", statusCode: StatusCodes.Status400BadRequest);

        return TypedResults.NoContent();
    }
}
