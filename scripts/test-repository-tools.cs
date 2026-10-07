#!/usr/bin/env dotnet
#:property PublishAot=false
#:property TreatWarningsAsErrors=true
#:include RepositoryTools.cs
#:include tests/RepositoryTests.cs

return await RepositoryTools.Tests.RepositoryTests.RunAsync();
