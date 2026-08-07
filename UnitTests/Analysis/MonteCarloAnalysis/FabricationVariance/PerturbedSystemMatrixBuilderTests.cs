using System.Numerics;
using CAP_Core.Analysis.MonteCarloAnalysis;
using CAP_Core.Analysis.MonteCarloAnalysis.FabricationVariance;
using CAP_Core.Components.Core;
using CAP_Core.Components.ComponentHelpers;
using CAP_Core.Components.FormulaReading;
using CAP_Core.Components.Process;
using CAP_Core.LightCalculation;
using CAP_Core;
using Shouldly;
using Xunit;

namespace UnitTests.Analysis.MonteCarloAnalysis.FabricationVariance
{
    public class PerturbedSystemMatrixBuilderTests
    {
        /// <summary>Inner builder stub returning a FRESH copy of the given entries per call.</summary>
        private sealed class StubBuilder : ISystemMatrixBuilder
        {
            private readonly List<Guid> _pins;
            private readonly Dictionary<(Guid, Guid), Complex> _entries;

            public StubBuilder(List<Guid> pins, Dictionary<(Guid, Guid), Complex> entries)
            {
                _pins = pins;
                _entries = entries;
            }

            public SMatrix GetSystemSMatrix(int LaserWaveLengthInNm)
            {
                var matrix = new SMatrix(_pins, new());
                matrix.SetValues(new(_entries));
                return matrix;
            }
        }

        private static (Component Component, StubBuilder Inner, (Guid, Guid) Key) CreateStraightSetup(
            Complex nominal)
        {
            var component = TestComponentFactory.CreateStraightWaveGuide();
            var pins = component.GetAllPins()
                .SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow })
                .ToList();
            var key = (component.GetAllPins()[0].IDInFlow, component.GetAllPins()[1].IDOutFlow);
            var inner = new StubBuilder(pins, new() { { key, nominal } });
            return (component, inner, key);
        }

        private static FabricationVarianceSource CreateAppliedSource(Component component)
        {
            var source = new FabricationVarianceSource(
                new[] { component }, ProcessTolerances.Default);
            source.ApplyVariance(new GaussianSampler(42));
            return source;
        }

        [Fact]
        public void GetSystemSMatrix_NoActiveSample_PassesThroughUnchanged()
        {
            var (component, inner, key) = CreateStraightSetup(new Complex(0.9, 0));
            var source = new FabricationVarianceSource(
                new[] { component }, ProcessTolerances.Default);
            var builder = new PerturbedSystemMatrixBuilder(inner, source);

            var matrix = builder.GetSystemSMatrix(StandardWaveLengths.RedNM);

            matrix.GetNonNullValues()[key].ShouldBe(new Complex(0.9, 0));
        }

        [Fact]
        public void GetSystemSMatrix_ActiveSample_AppliesLossAndPhase()
        {
            var nominal = new Complex(0.9, 0);
            var (component, inner, key) = CreateStraightSetup(nominal);
            var source = CreateAppliedSource(component);
            var builder = new PerturbedSystemMatrixBuilder(inner, source);

            var perturbed = builder.GetSystemSMatrix(StandardWaveLengths.RedNM).GetNonNullValues()[key];

            perturbed.ShouldNotBe(nominal);
            perturbed.Magnitude.ShouldBeLessThan(nominal.Magnitude);
            perturbed.Phase.ShouldNotBe(nominal.Phase);
        }

        [Fact]
        public void GetSystemSMatrix_PerturbedMagnitude_NeverExceedsPassivityCap()
        {
            var nominal = new Complex(1.0, 0);
            var (component, inner, key) = CreateStraightSetup(nominal);
            var source = new FabricationVarianceSource(
                new[] { component }, new ProcessTolerances(50, 25));
            var builder = new PerturbedSystemMatrixBuilder(inner, source);
            var sampler = new GaussianSampler(7);

            for (int run = 0; run < 50; run++)
            {
                source.ApplyVariance(sampler);
                var perturbed = builder.GetSystemSMatrix(StandardWaveLengths.RedNM)
                    .GetNonNullValues()[key];
                perturbed.Magnitude.ShouldBeLessThanOrEqualTo(1.0 + 1e-12);
            }
        }

        [Fact]
        public void GetSystemSMatrix_InterComponentEntries_AreNotPerturbed()
        {
            var componentA = TestComponentFactory.CreateStraightWaveGuide();
            var componentB = TestComponentFactory.CreateStraightWaveGuide();
            var pins = new[] { componentA, componentB }
                .SelectMany(c => c.GetAllPins())
                .SelectMany(p => new[] { p.IDInFlow, p.IDOutFlow })
                .ToList();
            // Waveguide-style link FROM a pin of A TO a pin of B — owned by neither alone.
            var linkKey = (componentA.GetAllPins()[1].IDInFlow, componentB.GetAllPins()[0].IDOutFlow);
            var inner = new StubBuilder(pins, new() { { linkKey, Complex.One } });
            var source = new FabricationVarianceSource(
                new[] { componentA, componentB }, ProcessTolerances.Default);
            source.ApplyVariance(new GaussianSampler(3));
            var builder = new PerturbedSystemMatrixBuilder(inner, source);

            var matrix = builder.GetSystemSMatrix(StandardWaveLengths.RedNM);

            matrix.GetNonNullValues()[linkKey].ShouldBe(Complex.One);
        }

        [Fact]
        public void GetSystemSMatrix_NonLinearEntries_AreWrappedWithTheFactor()
        {
            var (component, inner, key) = CreateStraightSetup(new Complex(0.9, 0));
            var source = CreateAppliedSource(component);
            var builder = new PerturbedSystemMatrixBuilder(inner, source);
            var baseMatrix = inner.GetSystemSMatrix(StandardWaveLengths.RedNM);
            baseMatrix.NonLinearConnections[key] = new ConnectionFunction(
                _ => new Complex(0.8, 0), "0.8", new List<Guid>(), false);
            var wrappingInner = new PassThroughBuilder(baseMatrix);
            var wrappingBuilder = new PerturbedSystemMatrixBuilder(wrappingInner, source);

            var matrix = wrappingBuilder.GetSystemSMatrix(StandardWaveLengths.RedNM);
            var wrapped = matrix.NonLinearConnections[key].CalcConnectionWeightAsync(new List<object>());

            wrapped.Magnitude.ShouldBeLessThan(0.8);
            wrapped.Phase.ShouldNotBe(0.0);
        }

        /// <summary>Inner builder stub returning the SAME matrix instance (for nonlinear wrap tests).</summary>
        private sealed class PassThroughBuilder : ISystemMatrixBuilder
        {
            private readonly SMatrix _matrix;
            public PassThroughBuilder(SMatrix matrix) => _matrix = matrix;
            public SMatrix GetSystemSMatrix(int LaserWaveLengthInNm) => _matrix;
        }
    }
}
