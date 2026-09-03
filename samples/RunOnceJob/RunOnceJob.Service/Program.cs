var greeting = Environment.GetEnvironmentVariable("GREETING") ?? "Hello from Dokploy.";
Console.WriteLine(greeting);
