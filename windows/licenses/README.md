The self-contained Windows package includes Microsoft .NET and the
`System.Management` / `System.CodeDom` components from dotnet/runtime.

The notices in this directory were retrieved from Microsoft's official
dotnet/runtime `v10.0.11` release:

- https://github.com/dotnet/runtime/blob/v10.0.11/LICENSE.TXT
- https://github.com/dotnet/runtime/blob/v10.0.11/THIRD-PARTY-NOTICES.TXT

The project references System.Management 10.0.0. Its package metadata identifies
the same MIT license and dotnet/runtime source repository. .NET SDK builds may
select a later .NET 10 servicing runtime; retain the license and notices included
with that servicing release when updating the SDK/runtime beyond this release.
