#!/usr/bin/env dotnet run
#:property PublishAot=false
#:include deploy-alpha.lib.cs

return await TerraScale.Deployment.AlphaDeployment.RunAsync(args);
