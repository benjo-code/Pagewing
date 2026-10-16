using Pagewing.Core;

if (args is ["--version"])
{
    Console.WriteLine(AppInfo.Version);
    return 0;
}

Console.Error.WriteLine("Usage: pagewing --version");
return 2;
