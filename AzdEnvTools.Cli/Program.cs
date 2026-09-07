using AzdEnvTools.Cli;

// Never follow a redirect to a login page or another host with a PAT-bearing request.
using var handler = new HttpClientHandler { AllowAutoRedirect = false };
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
return await CliApplication.RunAsync(args, http, Environment.GetEnvironmentVariable, Console.Out, Console.Error);
