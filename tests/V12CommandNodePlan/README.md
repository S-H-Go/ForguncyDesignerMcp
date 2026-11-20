# V12 server command node plan

Run on Windows with .NET 10 SDK and the exact supported V12 installation:

~~~powershell
dotnet run --project tests/V12CommandNodePlan -- '<12.0.102.0 SDK root>'
~~~

The fixture uses a disposable in-memory native command tree to verify that the real native resolver produces exactly one existing command owner for `serverCommand.updateServerCommands`, that the shared preflight/dispatch capture requires its existing ownership, and that missing or ambiguous targets fail closed. It does not start Designer, perform a collaboration checkout, apply a command update, or test live source persistence.
