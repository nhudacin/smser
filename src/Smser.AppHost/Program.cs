using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Azurite in a container, started and torn down with the app host. RunAsEmulator applies
// to local runs only — publishing this model targets a real storage account, so the same
// AppHost describes both without an if.
//
// This needs a container runtime (Docker Desktop or Podman). To run without one, start
// the web project directly: it falls back to UseDevelopmentStorage=true from
// appsettings.Development.json and talks to a locally installed Azurite. See the README.
var storage = builder.AddAzureStorage("storage")
                     .RunAsEmulator(emulator => emulator
                        // Without a volume the emulator's data lives in the container's
                        // writable layer, so every restart of the app host silently
                        // empties the roster table. Saved links surviving a restart is
                        // the whole reason they are links.
                        .WithDataVolume("smser-azurite"));

var tables = storage.AddTables("tables");

builder.AddProject<Projects.Smser_Web>("smser-web")
       .WithReference(tables)
       // Without WaitFor, the web app starts while Azurite is still coming up and the
       // first save fails with a connection error rather than waiting.
       .WaitFor(tables)
       .WithExternalHttpEndpoints();

builder.Build().Run();
