var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithPgAdmin(x=> x.WithHostPort(25432));

var minimapsDb = postgres.AddDatabase("minimaps-database");

// migrations after postgres/before app laod
var migrationService = builder.AddProject<Projects.Minimaps_CLI>("minimaps-cli")
    .WithReference(minimapsDb)
    .WithArgs("migrate")
    .WaitFor(minimapsDb);

// web services
var webapi = builder.AddProject<Projects.Minimaps_Web_API>("minimaps-web-api")
	.WithReference(minimapsDb)
	.WaitFor(migrationService);

var frontend = builder.AddProject<Projects.Minimaps_Web_Frontend>("minimaps-web-frontend")
	.WaitFor(webapi);

builder.Build().Run();
