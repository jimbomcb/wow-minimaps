var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithPgAdmin(x=> {
        x.WithHostPort(25432);
    });

var minimapsDb = postgres.AddDatabase("minimaps-database");

// migrations after postgres/before app load
var migrationService = builder.AddProject<Projects.Minimaps_CLI>("migration")
    .WithReference(minimapsDb)
    .WithArgs("migrate")
    .WaitFor(minimapsDb);

// web services
var webapi = builder.AddProject<Projects.Minimaps_Web_API>("web-api")
	.WithReference(minimapsDb)
	.WaitFor(migrationService);

var frontend = builder.AddProject<Projects.Minimaps_Web_Frontend>("web-frontend")
	.WaitFor(webapi);

// background services
var services = builder.AddProject<Projects.Minimaps_Services>("services")
    .WithReference(minimapsDb)
    .WithReference(webapi)
    .WaitFor(webapi);

builder.Build().Run();
