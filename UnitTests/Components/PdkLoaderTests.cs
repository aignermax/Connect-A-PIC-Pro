using Xunit;
using Shouldly;
using CAP_DataAccess.Components.ComponentDraftMapper;
using CAP_DataAccess.Components.ComponentDraftMapper.DTOs;
using System.Linq;

namespace UnitTests.Components
{
    public class PdkLoaderTests
    {
        [Fact]
        public void LoadFromJson_ValidPdk_ReturnsCorrectPdkData()
        {
            var loader = new PdkLoader();
            var json = @"{
                ""fileFormatVersion"": 1,
                ""name"": ""Test PDK"",
                ""description"": ""Test description"",
                ""foundry"": ""Test Foundry"",
                ""version"": ""1.0.0"",
                ""defaultWavelengthNm"": 1550,
                ""nazcaModuleName"": ""test_pdk"",
                ""components"": [
                    {
                        ""name"": ""MMI 2x2"",
                        ""category"": ""Couplers"",
                        ""nazcaFunction"": ""test_pdk.mmi2x2"",
                        ""widthMicrometers"": 100,
                        ""heightMicrometers"": 50,
                        ""nazcaOriginOffsetX"": 0, ""nazcaOriginOffsetY"": 25,
                        ""pins"": [
                            { ""name"": ""a0"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 12.5, ""angleDegrees"": 180 },
                            { ""name"": ""b0"", ""offsetXMicrometers"": 100, ""offsetYMicrometers"": 12.5, ""angleDegrees"": 0 }
                        ]
                    }
                ]
            }";

            var pdk = loader.LoadFromJson(json);

            pdk.Name.ShouldBe("Test PDK");
            pdk.Description.ShouldBe("Test description");
            pdk.Foundry.ShouldBe("Test Foundry");
            pdk.Version.ShouldBe("1.0.0");
            pdk.DefaultWavelengthNm.ShouldBe(1550);
            pdk.NazcaModuleName.ShouldBe("test_pdk");
            pdk.Components.Count.ShouldBe(1);
        }

        [Fact]
        public void LoadFromJson_ValidComponent_ParsesPinsCorrectly()
        {
            var loader = new PdkLoader();
            var json = @"{
                ""name"": ""Test PDK"",
                ""components"": [
                    {
                        ""name"": ""Test Component"",
                        ""category"": ""Test"",
                        ""nazcaFunction"": ""test.comp"",
                        ""widthMicrometers"": 200,
                        ""heightMicrometers"": 100,
                        ""nazcaOriginOffsetX"": 0, ""nazcaOriginOffsetY"": 50,
                        ""pins"": [
                            { ""name"": ""in"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 50, ""angleDegrees"": 180 },
                            { ""name"": ""out1"", ""offsetXMicrometers"": 200, ""offsetYMicrometers"": 25, ""angleDegrees"": 0 },
                            { ""name"": ""out2"", ""offsetXMicrometers"": 200, ""offsetYMicrometers"": 75, ""angleDegrees"": 0 }
                        ]
                    }
                ]
            }";

            var pdk = loader.LoadFromJson(json);
            var comp = pdk.Components[0];

            comp.Name.ShouldBe("Test Component");
            comp.Category.ShouldBe("Test");
            comp.NazcaFunction.ShouldBe("test.comp");
            comp.WidthMicrometers.ShouldBe(200);
            comp.HeightMicrometers.ShouldBe(100);
            comp.Pins.Count.ShouldBe(3);

            comp.Pins[0].Name.ShouldBe("in");
            comp.Pins[0].OffsetXMicrometers.ShouldBe(0);
            comp.Pins[0].OffsetYMicrometers.ShouldBe(50);
            comp.Pins[0].AngleDegrees.ShouldBe(180);

            comp.Pins[1].Name.ShouldBe("out1");
            comp.Pins[1].OffsetXMicrometers.ShouldBe(200);
            comp.Pins[1].AngleDegrees.ShouldBe(0);
        }

        [Fact]
        public void LoadFromJson_WithSMatrix_ParsesSMatrixCorrectly()
        {
            var loader = new PdkLoader();
            var json = @"{
                ""name"": ""Test PDK"",
                ""components"": [
                    {
                        ""name"": ""Splitter"",
                        ""nazcaFunction"": ""test.splitter"",
                        ""widthMicrometers"": 100,
                        ""heightMicrometers"": 50,
                        ""nazcaOriginOffsetX"": 0, ""nazcaOriginOffsetY"": 25,
                        ""pins"": [
                            { ""name"": ""in"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 25, ""angleDegrees"": 180 },
                            { ""name"": ""out"", ""offsetXMicrometers"": 100, ""offsetYMicrometers"": 25, ""angleDegrees"": 0 }
                        ],
                        ""sMatrix"": {
                            ""wavelengthNm"": 1550,
                            ""connections"": [
                                { ""fromPin"": ""in"", ""toPin"": ""out"", ""magnitude"": 0.707, ""phaseDegrees"": 45 }
                            ]
                        }
                    }
                ]
            }";

            var pdk = loader.LoadFromJson(json);
            var comp = pdk.Components[0];

            comp.SMatrix.ShouldNotBeNull();
            comp.SMatrix.WavelengthNm.ShouldBe(1550);
            comp.SMatrix.Connections.Count.ShouldBe(1);
            comp.SMatrix.Connections[0].FromPin.ShouldBe("in");
            comp.SMatrix.Connections[0].ToPin.ShouldBe("out");
            comp.SMatrix.Connections[0].Magnitude.ShouldBe(0.707);
            comp.SMatrix.Connections[0].PhaseDegrees.ShouldBe(45);
        }

        [Fact]
        public void LoadFromJson_MissingName_ThrowsException()
        {
            var loader = new PdkLoader();
            var json = @"{
                ""components"": []
            }";

            Should.Throw<InvalidOperationException>(() => loader.LoadFromJson(json));
        }

        [Fact]
        public void LoadFromJson_ComponentWithoutPins_ThrowsException()
        {
            var loader = new PdkLoader();
            var json = @"{
                ""name"": ""Test PDK"",
                ""components"": [
                    {
                        ""name"": ""Invalid Component"",
                        ""nazcaFunction"": ""test.invalid"",
                        ""widthMicrometers"": 100,
                        ""heightMicrometers"": 50,
                        ""nazcaOriginOffsetX"": 0, ""nazcaOriginOffsetY"": 25,
                        ""pins"": []
                    }
                ]
            }";

            Should.Throw<PdkValidationException>(() => loader.LoadFromJson(json));
        }

        [Fact]
        public void LoadFromJson_ComponentWithInvalidDimensions_ThrowsException()
        {
            var loader = new PdkLoader();
            var json = @"{
                ""name"": ""Test PDK"",
                ""components"": [
                    {
                        ""name"": ""Invalid Component"",
                        ""nazcaFunction"": ""test.invalid"",
                        ""widthMicrometers"": 0,
                        ""heightMicrometers"": 50,
                        ""nazcaOriginOffsetX"": 0, ""nazcaOriginOffsetY"": 25,
                        ""pins"": [
                            { ""name"": ""a"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 25, ""angleDegrees"": 0 }
                        ]
                    }
                ]
            }";

            Should.Throw<PdkValidationException>(() => loader.LoadFromJson(json));
        }

        [Fact]
        public void LoadFromFile_DemoPdk_LoadsSuccessfully()
        {
            var loader = new PdkLoader();
            var demoPdkPath = Path.Combine(
                Directory.GetCurrentDirectory(),
                "..", "..", "..", "..",
                "CAP-DataAccess", "PDKs", "demo-pdk.json");

            if (!File.Exists(demoPdkPath))
            {
                return;
            }

            var pdk = loader.LoadFromFile(demoPdkPath);

            pdk.Name.ShouldBe("Demo PDK");
            pdk.Components.Count.ShouldBeGreaterThan(0);

            foreach (var comp in pdk.Components)
            {
                comp.Pins.Count.ShouldBeGreaterThan(0);
                comp.WidthMicrometers.ShouldBeGreaterThan(0);
                comp.HeightMicrometers.ShouldBeGreaterThan(0);
            }
        }

        [Fact]
        public void LoadFromFile_SetsFilePathOnTheReturnedDraft()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lunima-pdkloader-filepath-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "fab.json");
            File.WriteAllText(path, @"{ ""name"": ""FilePathTest"", ""components"": [] }");

            try
            {
                var loader = new PdkLoader();

                loader.LoadFromFile(path).FilePath.ShouldBe(path);
                loader.LoadFromFileForEditing(path).FilePath.ShouldBe(path);
                loader.LoadFromJson(File.ReadAllText(path)).FilePath.ShouldBeNull();
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Fact]
        public void LoadFromJson_MultipleComponents_LoadsAll()
        {
            var loader = new PdkLoader();
            var json = @"{
                ""name"": ""Multi Component PDK"",
                ""components"": [
                    {
                        ""name"": ""Component A"",
                        ""category"": ""Cat1"",
                        ""nazcaFunction"": ""pdk.a"",
                        ""widthMicrometers"": 100,
                        ""heightMicrometers"": 50,
                        ""nazcaOriginOffsetX"": 0, ""nazcaOriginOffsetY"": 25,
                        ""pins"": [{ ""name"": ""p1"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 25, ""angleDegrees"": 180 }]
                    },
                    {
                        ""name"": ""Component B"",
                        ""category"": ""Cat2"",
                        ""nazcaFunction"": ""pdk.b"",
                        ""widthMicrometers"": 200,
                        ""heightMicrometers"": 100,
                        ""nazcaOriginOffsetX"": 0, ""nazcaOriginOffsetY"": 50,
                        ""pins"": [{ ""name"": ""p1"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 50, ""angleDegrees"": 180 }]
                    },
                    {
                        ""name"": ""Component C"",
                        ""category"": ""Cat1"",
                        ""nazcaFunction"": ""pdk.c"",
                        ""widthMicrometers"": 150,
                        ""heightMicrometers"": 75,
                        ""nazcaOriginOffsetX"": 0, ""nazcaOriginOffsetY"": 37.5,
                        ""pins"": [{ ""name"": ""p1"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 37.5, ""angleDegrees"": 180 }]
                    }
                ]
            }";

            var pdk = loader.LoadFromJson(json);

            pdk.Components.Count.ShouldBe(3);
            pdk.Components[0].Name.ShouldBe("Component A");
            pdk.Components[1].Name.ShouldBe("Component B");
            pdk.Components[2].Name.ShouldBe("Component C");
        }

        [Fact]
        public void LoadFromFile_SiEPICEBeamPdk_LoadsAllComponents()
        {
            var loader = new PdkLoader();
            var siepicPdkPath = Path.Combine(
                Directory.GetCurrentDirectory(),
                "..", "..", "..", "..",
                "CAP-DataAccess", "PDKs", "siepic-ebeam-pdk.json");

            if (!File.Exists(siepicPdkPath))
            {
                return;
            }

            var pdk = loader.LoadFromFile(siepicPdkPath);

            pdk.Name.ShouldBe("SiEPIC EBeam PDK");
            pdk.Foundry.ShouldBe("UBC / SiEPIC");
            pdk.DefaultWavelengthNm.ShouldBe(1550);
            pdk.NazcaModuleName.ShouldBe("siepic_ebeam_pdk");

            pdk.Components.Count.ShouldBe(44);

            foreach (var comp in pdk.Components)
            {
                comp.Name.ShouldNotBeNullOrWhiteSpace();
                comp.Category.ShouldNotBeNullOrWhiteSpace();
                comp.NazcaFunction.ShouldNotBeNullOrWhiteSpace();
                comp.Pins.Count.ShouldBeGreaterThan(0);
                comp.WidthMicrometers.ShouldBeGreaterThan(0);
                comp.HeightMicrometers.ShouldBeGreaterThan(0);
            }

            var componentNames = pdk.Components.Select(c => c.Name).ToList();
            componentNames.ShouldContain("Y-Branch 1550");
            componentNames.ShouldContain("Y-Branch 895");
            componentNames.ShouldContain("GC SiN TE 1310 8deg");
            componentNames.ShouldContain("Crossing Horizontal");
            componentNames.ShouldContain("Adiabatic Coupler TE 1550");
            componentNames.ShouldContain("Bond Pad");
        }

        [Fact]
        public void LoadFromJson_GdsFactoryBackendComponent_LoadsWithoutNazcaFunctionOrOffset()
        {
            var loader = new PdkLoader();
            var json = @"{
                ""fileFormatVersion"": 1,
                ""name"": ""SiN PDK"",
                ""backend"": ""gdsfactory"",
                ""defaultWavelengthNm"": 1550,
                ""components"": [
                    {
                        ""name"": ""Straight"",
                        ""category"": ""Waveguides"",
                        ""gdsFactoryFunction"": ""cspdk.sin300.straight"",
                        ""widthMicrometers"": 10,
                        ""heightMicrometers"": 1.2,
                        ""pins"": [
                            { ""name"": ""o1"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 0.6, ""angleDegrees"": 180 },
                            { ""name"": ""o2"", ""offsetXMicrometers"": 10, ""offsetYMicrometers"": 0.6, ""angleDegrees"": 0 }
                        ]
                    }
                ]
            }";

            var pdk = loader.LoadFromJson(json);

            pdk.IsGdsFactoryBackend.ShouldBeTrue();
            pdk.Components.Single().GdsFactoryFunction.ShouldBe("cspdk.sin300.straight");
        }

        [Fact]
        public void LoadFromJson_GdsFactoryBackendComponentMissingFactory_Throws()
        {
            var loader = new PdkLoader();
            var json = @"{
                ""fileFormatVersion"": 1,
                ""name"": ""Broken SiN PDK"",
                ""backend"": ""gdsfactory"",
                ""defaultWavelengthNm"": 1550,
                ""components"": [
                    {
                        ""name"": ""Nameless"",
                        ""category"": ""Waveguides"",
                        ""widthMicrometers"": 10,
                        ""heightMicrometers"": 1.2,
                        ""pins"": [
                            { ""name"": ""o1"", ""offsetXMicrometers"": 0, ""offsetYMicrometers"": 0.6, ""angleDegrees"": 180 }
                        ]
                    }
                ]
            }";

            Should.Throw<PdkValidationException>(() => loader.LoadFromJson(json));
        }
    }
}
