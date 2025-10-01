
var builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<IResourceWithConnectionString> minimapsDb;

const bool useManualConnectionString = true;
if (!useManualConnectionString)
{
    var postgres = builder.AddPostgres("postgres")
        //.WithDataVolume("minimap-postgres-persist")
        .WithPgAdmin(x => {
            x.WithHostPort(25432);
        });
    minimapsDb = postgres.AddDatabase("minimaps-database");
}
else
{
    // Pull from ConnectionStrings__minimaps-database rather than docker contaienr for nwo
    minimapsDb = builder.AddConnectionString("minimaps-database");
}


// migrations after postgres/before app load
var migrationService = builder.AddProject<Projects.Minimaps_CLI>("migration")
        .WithReference(minimapsDb)
        .WithArgs("migrate")
        .WaitFor(minimapsDb);

builder.AddProject<Projects.Minimaps_Services>("services")
    .WithReference(minimapsDb)
    .WaitFor(migrationService);

builder.AddProject<Projects.Minimaps_Frontend>("minimaps-frontend")
    .WithReference(minimapsDb)
    .WaitFor(migrationService);

builder.Build().Run();
