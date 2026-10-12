#!/usr/bin/env dotnet run
#:property PublishAot=false
#:include provision-reviewer-secrets.lib.cs

return await TerraScale.Deployment.ReviewerSecrets.RunAsync(args);
