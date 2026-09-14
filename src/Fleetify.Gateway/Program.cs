using Fleetify.Gateway;

// fleetify-gateway: the agent endpoint of a Fleeto instance. See GatewayApplication for the wiring.
var app = GatewayApplication.Build(args);
await app.RunAsync();
