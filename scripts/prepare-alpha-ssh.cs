#!/usr/bin/env dotnet run
#:property PublishAot=false
#:include prepare-alpha-ssh.lib.cs

return await TerraScale.Deployment.AlphaSsh.RunAsync();
