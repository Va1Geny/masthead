using Isle.Core;
using Isle.Modules.Kube;
using Isle.Modules.Setup;

namespace Isle.Tests;

public class SetupTests
{
    const string Profile = @"C:\u";

    [Fact]
    public void Detect_maps_binaries_folders_and_the_docker_pipe()
    {
        var found = SetupModel.Detect(exe => exe == "gh", p => p == Profile + @"\.codex" || p == @"\\.\pipe\docker_engine", Profile);
        Assert.Equal(["codex"], found["agent"]);
        Assert.Equal(["github"], found["git"]);
        Assert.Equal(["docker"], found["containers"]);
        var none = SetupModel.Detect(_ => false, _ => false, Profile);
        Assert.All(none.Values, v => Assert.Empty(v));
    }

    [Fact]
    public void Preselect_takes_the_first_detected_choice_else_none()
    {
        var found = SetupModel.Detect(exe => exe is "claude" or "codex", _ => false, Profile);
        var pick = SetupModel.Preselect(found);
        Assert.Equal("claude", pick["agent"]);
        Assert.Equal("none", pick["git"]);
        Assert.Equal("none", pick["containers"]);
    }

    [Fact]
    public void Defaults_follow_the_agent_and_kubeconfig()
    {
        Assert.Equal(new[] { "agents", "logs", "media", "ports", "projects", "sysmon", "usage" }, SetupModel.Defaults("claude", false).Order());
        Assert.Contains("kube", SetupModel.Defaults("codex", true));
        Assert.Contains("docker", SetupModel.Defaults("codex", false, containers: true));
        var bare = SetupModel.Defaults("none", false);
        Assert.Equal(new[] { "logs", "media", "ports", "projects", "sysmon" }, bare.Order());
    }

    [Fact]
    public void Limit_blocks_new_ticks_but_not_unticking()
    {
        var ticked = Enumerable.Range(0, 3).Select(i => "m" + i).ToHashSet();
        Assert.False(SetupModel.CanTick(ticked, "extra", limit: 3));
        Assert.True(SetupModel.CanTick(ticked, "m1", limit: 3));
        Assert.True(SetupModel.CanTick(ticked, "extra", limit: 4));
    }

    [Fact]
    public void Apply_writes_module_switches_and_answers_only()
    {
        var s = new Settings();
        var answers = new Dictionary<string, string> { ["agent"] = "codex", ["git"] = "none", ["containers"] = "docker" };
        SetupModel.Apply(s, answers, new HashSet<string> { "sysmon", "usage", "kube" });
        Assert.True(s.Modules["sysmon"]);
        Assert.True(s.Modules["usage"]);
        Assert.True(s.Modules["kube"]);
        Assert.False(s.Modules["ports"]);
        Assert.Equal(SetupModel.Items.Length, s.Modules.Count);
        Assert.Equal("codex", s.Opt("setup.agent"));
        Assert.Equal("docker", s.Opt("setup.containers"));
        Assert.True(s.OptBool("setup.done"));
    }

    [Fact]
    public void Without_an_agent_agents_and_usage_stay_off_even_if_ticked()
    {
        var s = new Settings();
        SetupModel.Apply(s, new Dictionary<string, string> { ["agent"] = "none", ["git"] = "none", ["containers"] = "none" }, new HashSet<string> { "agents", "usage", "ports" });
        Assert.False(s.Modules["agents"]);
        Assert.False(s.Modules["usage"]);
        Assert.True(s.Modules["ports"]);
    }

    [Fact]
    public void Initial_reopens_with_the_saved_choices_after_setup_was_done()
    {
        var found = SetupModel.Detect(_ => false, _ => false, Profile);
        var s = new Settings();
        Assert.Equal("none", SetupModel.Initial(s, found, false).Answers["agent"]);
        SetupModel.Apply(s, new Dictionary<string, string> { ["agent"] = "claude", ["git"] = "github", ["containers"] = "none" }, new HashSet<string> { "sysmon", "agents" });
        var (answers, ticked) = SetupModel.Initial(s, found, false);
        Assert.Equal("claude", answers["agent"]);
        Assert.Equal("github", answers["git"]);
        Assert.Equal(["agents", "sysmon"], ticked.Order());
    }

    [Fact]
    public void Kube_indicator_stays_below_the_mic_camera_privacy_indicator()
    {
        Assert.True(KubeModule.Indicator("x").Priority < PillPriority.MicCamera);
    }
}
