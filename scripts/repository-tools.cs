#!/usr/bin/env dotnet
#:include RepositoryTools.cs
#:property TreatWarningsAsErrors=true
#:property PublishAot=false

using RepositoryTools;

return await ToolCommands.RunAsync(args);
