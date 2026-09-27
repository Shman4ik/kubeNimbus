using System.Text.Json;
using KubeNimbus.App.ViewModels;
using KubeNimbus.Core;

namespace KubeNimbus.App.Tests;

/// <summary>
/// FEAT-45: a single <c>configMapKeyRef</c>/<c>secretKeyRef</c> env row opens the object it
/// names, through the same resolve-and-open path the <c>envFrom</c> lines and owner chips
/// use — and only the rows that name an object offer it.
/// </summary>
public class EnvSourceLinkTests
{
    private static DynamicResource Pod() => new(JsonDocument.Parse("""
        {"apiVersion":"v1","kind":"Pod","metadata":{"name":"api-1","namespace":"shop","uid":"u"},
         "spec":{"containers":[{"name":"app","env":[
           {"name":"LOG_LEVEL","valueFrom":{"configMapKeyRef":{"name":"app-config","key":"log-level"}}},
           {"name":"DB_PASSWORD","valueFrom":{"secretKeyRef":{"name":"db-creds","key":"password"}}},
           {"name":"MODE","value":"prod"},
           {"name":"POD_IP","valueFrom":{"fieldRef":{"fieldPath":"status.podIP"}}}]}]},
         "status":{"phase":"Running","podIP":"10.0.0.7"}}
        """).RootElement.Clone());

    private static (PodDetailTabViewModel Detail, List<(OwnerRef Owner, string? Namespace)> Opened) Detail()
    {
        TestObjects.RedirectStores();
        var opened = new List<(OwnerRef, string?)>();
        var detail = new PodDetailTabViewModel(
            null, new ResourceRowViewModel(Pod()), _ => { }, (owner, ns) => { opened.Add((owner, ns)); return Task.CompletedTask; });
        return (detail, opened);
    }

    private static EnvVarViewModel Var(PodDetailTabViewModel detail, string name) =>
        detail.EnvironmentVars.Single(v => v.Name == name);

    [Test]
    public async Task A_configmap_key_row_opens_its_configmap_in_the_pods_namespace()
    {
        var (detail, opened) = Detail();

        await detail.OpenEnvVarSourceCommand.ExecuteAsync(Var(detail, "LOG_LEVEL"));

        var (owner, ns) = opened.Single();
        await Assert.That(owner.Kind).IsEqualTo("ConfigMap");
        await Assert.That(owner.Name).IsEqualTo("app-config");
        await Assert.That(owner.ApiVersion).IsEqualTo("v1");
        await Assert.That(ns).IsEqualTo("shop");
    }

    [Test]
    public async Task A_secret_key_row_opens_its_secret_and_reveals_nothing_on_the_way()
    {
        var (detail, opened) = Detail();
        var password = Var(detail, "DB_PASSWORD");

        await detail.OpenEnvVarSourceCommand.ExecuteAsync(password);

        await Assert.That(opened.Single().Owner.Kind).IsEqualTo("Secret");
        await Assert.That(opened.Single().Owner.Name).IsEqualTo("db-creds");
        await Assert.That(password.IsMasked).IsTrue();
        await Assert.That(password.RevealedValue).IsNull();
    }

    [Test]
    public async Task Only_rows_that_name_an_object_offer_to_open_one()
    {
        var (detail, opened) = Detail();

        await Assert.That(Var(detail, "LOG_LEVEL").CanOpenSource).IsTrue();
        await Assert.That(Var(detail, "DB_PASSWORD").CanOpenSource).IsTrue();
        await Assert.That(Var(detail, "MODE").CanOpenSource).IsFalse();
        await Assert.That(Var(detail, "POD_IP").CanOpenSource).IsFalse();

        await detail.OpenEnvVarSourceCommand.ExecuteAsync(Var(detail, "MODE"));
        await Assert.That(opened.Count).IsEqualTo(0);
    }
}
