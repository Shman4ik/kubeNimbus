using System.Net;
using System.Text.Json;

namespace KubeNimbus.Core;

/// <summary>
/// Reading one kind's <c>additionalPrinterColumns</c> off the cluster.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a GET of the CRD and not something cleverer.</b> The columns are not in the
/// discovery document — discovery reports names, verbs and subresources, and says
/// nothing about how a kind should be printed — so they have to come from the
/// <c>CustomResourceDefinition</c> object itself. Two alternatives were considered:
/// </para>
/// <list type="bullet">
/// <item><b>Listing every CRD at connect time</b> would be one request instead of one
/// per kind, but a CRD object carries its whole OpenAPI schema; on a cluster with
/// cert-manager, Argo and Istio installed that list is tens of megabytes, fetched to
/// answer a question about kinds the user may never open. This is lazy for that
/// reason: one small GET the first time a kind is selected, cached by the caller.</item>
/// <item><b>Asking the API server for a Table</b> (<c>Accept:
/// application/json;as=Table;v=v1;g=meta.k8s.io</c>) is what kubectl does, and would
/// give byte-identical columns for CRDs <em>and</em> built-ins. It is rejected because
/// this app's list is a watch, not a get: a Table response is a snapshot of rendered
/// strings with no object behind it, so the informer, the YAML editor, the row actions
/// and every status pill would have to be fed from somewhere else. It would also take
/// the built-in kinds away from <c>ResourceStatusSummary</c>, which this change is
/// explicitly not allowed to disturb.</item>
/// </list>
/// <para>
/// <b>Absence is the normal answer, not an error.</b> A built-in kind, an aggregated
/// API (metrics.k8s.io and friends are not CRDs), a cluster with no read access to
/// <c>apiextensions.k8s.io</c>, or an apiextensions API that isn't served at all — all
/// four come back empty and the list renders exactly as it did before this existed.
/// Nothing here ever throws at the caller.
/// </para>
/// </remarks>
public sealed partial class ClusterClient
{
    /// <summary>
    /// The printer columns declared for <paramref name="descriptor"/>'s kind at
    /// <paramref name="descriptor"/>'s version, or empty when there are none to have.
    /// </summary>
    public async Task<IReadOnlyList<PrinterColumn>> GetPrinterColumnsAsync(
        ResourceDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        // A CustomResourceDefinition must declare a non-empty API group (the CRD
        // validator requires it), so nothing in the core group can be one. That is a
        // rule of the API, not a list of built-in kinds — the sort of shortcut this
        // repo does allow.
        if (string.IsNullOrEmpty(descriptor.Group))
        {
            return [];
        }

        // A CRD's own name is required to be exactly `<plural>.<group>`, so the kind
        // we are looking at names the object we need with no search.
        var name = $"{descriptor.Plural}.{descriptor.Group}";
        var path = $"apis/apiextensions.k8s.io/v1/customresourcedefinitions/{Uri.EscapeDataString(name)}";

        try
        {
            using var response = await SendRequestAsync(
                HttpMethod.Get, path, content: null, HttpCompletionOption.ResponseContentRead,
                cancellationToken).ConfigureAwait(false);

            // 404: not a CRD (a built-in, or an aggregated API), or apiextensions isn't
            // served. 403: no read access to CRDs, which plenty of scoped users have.
            // Both mean "no columns", and both are ordinary.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden
                or HttpStatusCode.Unauthorized
                || !response.IsSuccessStatusCode)
            {
                return [];
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return PrinterColumns.Parse(document.RootElement, descriptor.Version);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A transport failure, a proxy returning HTML, an apiextensions version this
            // server doesn't serve. The list is not about to be blocked on how it prints.
            return [];
        }
    }
}
