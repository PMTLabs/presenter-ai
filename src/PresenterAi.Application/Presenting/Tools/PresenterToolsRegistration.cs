using PresenterAi.Application.Tools;

namespace PresenterAi.Application.Presenting.Tools;

public static class PresenterToolsRegistration
{
    public static void RegisterAll(ToolRegistry registry, IPresenter presenter)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(presenter);

        if (!registry.Contains("pause_presentation"))
        {
            registry.Register(new PausePresentationTool(presenter));
        }

        if (!registry.Contains("resume_presentation"))
        {
            registry.Register(new ResumePresentationTool(presenter));
        }

        if (!registry.Contains("next_slide"))
        {
            registry.Register(new NextSlideTool(presenter));
        }

        if (!registry.Contains("previous_slide"))
        {
            registry.Register(new PreviousSlideTool(presenter));
        }

        if (!registry.Contains("go_to_slide"))
        {
            registry.Register(new GoToSlideTool(presenter));
        }

        if (!registry.Contains("end_presentation"))
        {
            registry.Register(new EndPresentationTool(presenter));
        }
    }
}
