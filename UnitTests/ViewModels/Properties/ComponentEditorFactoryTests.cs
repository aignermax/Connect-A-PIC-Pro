using System.Linq;
using CAP.Avalonia.ViewModels.Canvas;
using CAP.Avalonia.ViewModels.Library;
using CAP.Avalonia.ViewModels.Properties;
using CAP.Avalonia.ViewModels.Properties.Editors;
using Shouldly;
using Xunit;

namespace UnitTests.ViewModels.Properties
{
    /// <summary>
    /// Locks in the per-component property-editor pattern: each component
    /// type must reach the right editor ViewModel through the factory, and
    /// the generic fallback must catch everything else.
    /// </summary>
    public class ComponentEditorFactoryTests
    {
        private static ComponentEditorFactory BuildDefaultFactory()
            // Same order as App.axaml.cs: specific providers first, generic last.
            => new(new IComponentEditorProvider[]
            {
                new OnaAnalyzerEditorProvider(),
                new LightSourceEditorProvider(),
                new SliderEditorProvider(),
                new GenericComponentEditorProvider(),
            });

        private static ComponentViewModel BuildVm(ComponentTemplate template)
        {
            var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);
            return new ComponentViewModel(component, template.Name, template.PdkSource);
        }

        private static ComponentTemplate? FindTemplate(string name)
            => UnitTests.TestPdkLoader.LoadAllTemplates()
                .FirstOrDefault(t => t.Name == name);

        [Fact]
        public void CreateEditor_OnaAnalyzer_ReturnsOnaAnalyzerEditor()
        {
            var template = FindTemplate("ONA Analyzer");
            if (template == null) return; // CI guard

            var factory = BuildDefaultFactory();
            var editor = factory.CreateEditor(BuildVm(template));

            editor.ShouldBeOfType<OnaAnalyzerEditorViewModel>();
        }

        [Fact]
        public void CreateEditor_PhaseShifter_ReturnsSliderEditor()
        {
            var template = FindTemplate("Phase Shifter");
            if (template == null) return;

            var factory = BuildDefaultFactory();
            var editor = factory.CreateEditor(BuildVm(template));

            // Phase Shifter exposes a slider — must reach the slider editor,
            // not the light-source one (it's not a coupler) and not the
            // generic fallback.
            editor.ShouldBeOfType<SliderEditorViewModel>();
        }

        [Fact]
        public void CreateEditor_GratingCoupler_ReturnsLightSourceEditor()
        {
            var template = FindTemplate("Grating Coupler");
            if (template == null) return;

            var factory = BuildDefaultFactory();
            var editor = factory.CreateEditor(BuildVm(template));

            editor.ShouldBeOfType<LightSourceEditorViewModel>();
        }

        [Fact]
        public void CreateEditor_StraightWaveguide_FallsBackToGenericEditor()
        {
            // Pick any non-coupler, non-phase-shifter, non-analyzer template
            // from the demo PDK. "Straight" or similar passive waveguide.
            var template = UnitTests.TestPdkLoader.LoadAllTemplates()
                .FirstOrDefault(t =>
                    !t.HasSlider
                    && t.NazcaFunctionName != "__analyzer__"
                    && !(t.Name.Contains("Coupler") || t.Name.Contains("coupler")));
            if (template == null) return;

            var factory = BuildDefaultFactory();
            var editor = factory.CreateEditor(BuildVm(template));

            editor.ShouldBeOfType<GenericComponentEditorViewModel>();
        }

        [Fact]
        public void CreateEditor_NullSelection_ReturnsNull()
        {
            var factory = BuildDefaultFactory();
            factory.CreateEditor(null).ShouldBeNull();
        }

        [Fact]
        public void CreateEditor_ProviderOrderRespected_AnalyzerNotShadowedByGeneric()
        {
            // Regression guard: if Generic is registered before OnaAnalyzer
            // it shadows everything. The factory must walk the list as
            // given — first non-null wins.
            var template = FindTemplate("ONA Analyzer");
            if (template == null) return;

            var generic = new GenericComponentEditorProvider();
            var ona = new OnaAnalyzerEditorProvider();

            var rightOrder = new ComponentEditorFactory(new IComponentEditorProvider[] { ona, generic });
            var wrongOrder = new ComponentEditorFactory(new IComponentEditorProvider[] { generic, ona });

            var vm = BuildVm(template);
            rightOrder.CreateEditor(vm).ShouldBeOfType<OnaAnalyzerEditorViewModel>();
            // Wrong order: generic eats it first. The test PINS this behaviour
            // so future devs see that registration order matters.
            wrongOrder.CreateEditor(vm).ShouldBeOfType<GenericComponentEditorViewModel>();
        }

        [Theory]
        [InlineData("Grating Coupler")]
        [InlineData("Phase Shifter")]
        public void CreateEditor_SpecificComponents_MatchExactlyOneSpecificProvider(string templateName)
        {
            // Regression guard for issue #554: the old MainWindow.axaml carried
            // hard-coded "Laser Configuration" / "Component Parameter" sections
            // that duplicated the provider editors. With the migration done,
            // exactly ONE specific (non-generic) provider must claim the
            // component, so exactly one editor section is rendered.
            var template = FindTemplate(templateName);
            if (template == null) return; // CI guard

            var vm = BuildVm(template);
            var specificProviders = new IComponentEditorProvider[]
            {
                new OnaAnalyzerEditorProvider(),
                new LightSourceEditorProvider(),
                new SliderEditorProvider(),
            };

            var matches = specificProviders
                .Select(p => p.TryCreateEditor(vm))
                .Count(editor => editor != null);

            matches.ShouldBe(1);
        }

        [Fact]
        public void OnaAnalyzerEditor_OpenSweep_CallsWiredDelegate()
        {
            var template = FindTemplate("ONA Analyzer");
            if (template == null) return;

            var component = ComponentTemplates.CreateFromTemplate(template, 0, 0);
            CAP_Core.Components.Core.Component? captured = null;
            var editor = new OnaAnalyzerEditorViewModel(component)
            {
                OpenSweepAsync = c =>
                {
                    captured = c;
                    return Task.CompletedTask;
                }
            };

            editor.OpenSweepCommand.Execute(null);

            captured.ShouldBe(component);
        }
    }
}
