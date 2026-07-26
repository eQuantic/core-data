using eQuantic.Core.Data.Tools;

try
{
    return Cli.Run(args);
}
catch (ToolException failure)
{
    Console.Error.WriteLine(failure.Message);
    return 1;
}
