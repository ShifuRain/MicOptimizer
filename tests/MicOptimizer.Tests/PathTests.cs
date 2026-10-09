using Microsoft.Win32;
using MicOptimizer.Core.PathSetup;
using Xunit;

namespace MicOptimizer.Tests;

public class PathEditorTests
{
    private const string Dir = @"C:\Users\Someone\AppData\Local\Programs\Mic Optimizer";

    [Fact]
    public void Adds_the_folder_at_the_end_and_keeps_every_other_entry_exactly_as_it_was()
    {
        var result = PathEditor.Add(@"C:\Tools;%USERPROFILE%\bin;C:\Other", Dir);
        Assert.Equal(@"C:\Tools;%USERPROFILE%\bin;C:\Other;" + Dir, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(";;")]
    public void Adds_to_an_empty_path(string? existing) => Assert.Equal(Dir, PathEditor.Add(existing, Dir));

    [Fact]
    public void Does_not_leave_a_stray_separator_when_the_path_ends_with_one()
    {
        Assert.Equal(@"C:\Tools;" + Dir, PathEditor.Add(@"C:\Tools;", Dir));
    }

    [Theory]
    [InlineData(@"C:\Tools;C:\USERS\SOMEONE\APPDATA\LOCAL\PROGRAMS\MIC OPTIMIZER")]        // different case
    [InlineData(@"C:\Tools;C:\Users\Someone\AppData\Local\Programs\Mic Optimizer\")]       // trailing slash
    [InlineData("C:\\Tools;\"C:\\Users\\Someone\\AppData\\Local\\Programs\\Mic Optimizer\"")] // quoted
    public void Adding_twice_changes_nothing_however_the_existing_entry_is_written(string existing)
    {
        Assert.True(PathEditor.Contains(existing, Dir));
        Assert.Equal(existing, PathEditor.Add(existing, Dir));
    }

    [Fact]
    public void Environment_variables_are_expanded_when_comparing()
    {
        string profile = Environment.GetEnvironmentVariable("USERPROFILE")!;
        Assert.True(PathEditor.Contains(@"%USERPROFILE%\Tools", profile + @"\Tools"));
        Assert.False(PathEditor.Contains(@"%USERPROFILE%\Tools", profile + @"\Other"));
    }

    [Fact]
    public void Removes_only_the_exact_folder_not_ones_that_merely_start_with_it()
    {
        var path = $@"C:\Tools;{Dir};{Dir} Extras;C:\Other";
        Assert.Equal($@"C:\Tools;{Dir} Extras;C:\Other", PathEditor.Remove(path, Dir));
    }

    [Fact]
    public void Removes_every_copy_and_is_a_no_op_when_absent()
    {
        Assert.Equal(@"C:\Tools", PathEditor.Remove($@"{Dir};C:\Tools;{Dir}\", Dir));
        Assert.Equal(@"C:\Tools;C:\Other", PathEditor.Remove(@"C:\Tools;C:\Other", Dir));
        Assert.Equal("", PathEditor.Remove(null, Dir));
    }

    [Fact]
    public void Add_then_remove_returns_to_the_original()
    {
        const string original = @"C:\Tools;%SystemRoot%\system32;C:\Other";
        Assert.Equal(original, PathEditor.Remove(PathEditor.Add(original, Dir), Dir));
    }

    [Fact]
    public void A_blank_folder_is_rejected() => Assert.Throws<ArgumentException>(() => PathEditor.Add("C:\\Tools", " "));
}

/// <summary>Exercises the registry code against a throwaway key. The real user PATH is never touched.</summary>
public sealed class UserPathTests : IDisposable
{
    private readonly string _key = @"Software\MicOptimizerTests\" + Guid.NewGuid();
    private const string Dir = @"C:\Program Files\Mic Optimizer Test";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);

    private void Seed(string value, RegistryValueKind kind)
    {
        using var k = Registry.CurrentUser.CreateSubKey(_key);
        k.SetValue("Path", value, kind);
    }

    private (string value, RegistryValueKind kind) Raw()
    {
        using var k = Registry.CurrentUser.OpenSubKey(_key)!;
        return ((string)k.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames)!, k.GetValueKind("Path"));
    }

    [Fact]
    public void Add_and_remove_round_trip_without_touching_variables_or_the_value_type()
    {
        const string original = @"%USERPROFILE%\bin;C:\Tools";
        Seed(original, RegistryValueKind.ExpandString);
        var path = new UserPath(_key);

        Assert.False(path.Contains(Dir));
        Assert.True(path.Add(Dir));
        Assert.True(path.Contains(Dir));
        Assert.Equal((original + ";" + Dir, RegistryValueKind.ExpandString), Raw());       // %USERPROFILE% still unexpanded

        Assert.True(path.Remove(Dir));
        Assert.Equal((original, RegistryValueKind.ExpandString), Raw());
    }

    [Fact]
    public void Adding_twice_and_removing_when_absent_report_no_change()
    {
        Seed(@"C:\Tools", RegistryValueKind.ExpandString);
        var path = new UserPath(_key);

        Assert.True(path.Add(Dir));
        Assert.False(path.Add(Dir));
        Assert.True(path.Remove(Dir));
        Assert.False(path.Remove(Dir));
        Assert.Equal(@"C:\Tools", Raw().value);
    }

    [Fact]
    public void Creates_the_value_when_the_user_has_no_path_at_all()
    {
        var path = new UserPath(_key);
        Assert.False(path.Contains(Dir));
        Assert.True(path.Add(Dir));
        Assert.Equal((Dir, RegistryValueKind.ExpandString), Raw());
    }

    [Fact]
    public void Keeps_a_plain_string_value_a_plain_string_value()
    {
        Seed(@"C:\Tools", RegistryValueKind.String);
        new UserPath(_key).Add(Dir);
        Assert.Equal(RegistryValueKind.String, Raw().kind);
    }
}
