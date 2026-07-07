namespace UnitTests.ComponentRegistry;

/// <summary>
/// JSON fixtures mirroring the live photonic-registry repository
/// (github.com/aignermax/photonic-registry) so client tests run without network.
/// </summary>
public static class RegistryTestData
{
    /// <summary>Index listing the five generic-si220 demo components.</summary>
    public const string IndexJson = """
    {
      "schemaVersion": 1,
      "processes": [
        { "id": "generic-si220", "name": "Generic Silicon-on-Insulator 220 nm", "status": "demo" }
      ],
      "components": [
        {
          "id": "directional-coupler-2x2",
          "name": "Directional coupler 2x2 (~50/50 at 1550 nm)",
          "description": "Coupled-mode-theory model.",
          "process": "generic-si220",
          "portCount": 4,
          "path": "processes/generic-si220/components/directional-coupler-2x2/component.json",
          "tiers": { "geometry": false, "simulated": true, "measured": false },
          "bestStatus": "demo"
        },
        {
          "id": "mzi-unbalanced-dl40",
          "name": "Unbalanced Mach-Zehnder interferometer (dL = 40 um)",
          "description": "Two ideal 50/50 couplers.",
          "process": "generic-si220",
          "portCount": 4,
          "path": "processes/generic-si220/components/mzi-unbalanced-dl40/component.json",
          "tiers": { "geometry": false, "simulated": true, "measured": false },
          "bestStatus": "demo"
        },
        {
          "id": "ring-resonator-r10",
          "name": "All-pass ring resonator (R = 10 um)",
          "description": "All-pass microring.",
          "process": "generic-si220",
          "portCount": 2,
          "path": "processes/generic-si220/components/ring-resonator-r10/component.json",
          "tiers": { "geometry": false, "simulated": true, "measured": false },
          "bestStatus": "demo"
        },
        {
          "id": "straight-waveguide-100um",
          "name": "Straight waveguide (100 um)",
          "description": "Single-mode strip waveguide.",
          "process": "generic-si220",
          "portCount": 2,
          "path": "processes/generic-si220/components/straight-waveguide-100um/component.json",
          "tiers": { "geometry": false, "simulated": true, "measured": false },
          "bestStatus": "demo"
        },
        {
          "id": "y-branch-1x2",
          "name": "Y-branch splitter 1x2",
          "description": "Ideal 50/50 power splitter.",
          "process": "generic-si220",
          "portCount": 3,
          "path": "processes/generic-si220/components/y-branch-1x2/component.json",
          "tiers": { "geometry": false, "simulated": true, "measured": false },
          "bestStatus": "demo"
        }
      ]
    }
    """;

    /// <summary>Repo-relative path of the Y-branch manifest.</summary>
    public const string YBranchPath = "processes/generic-si220/components/y-branch-1x2/component.json";

    /// <summary>Y-branch component manifest with one simulated demo artifact.</summary>
    public const string YBranchJson = """
    {
      "id": "y-branch-1x2",
      "name": "Y-branch splitter 1x2",
      "description": "Ideal 50/50 power splitter with 0.2 dB excess loss.",
      "process": "generic-si220",
      "ports": [
        { "name": "o1", "kind": "optical", "description": "input" },
        { "name": "o2", "kind": "optical", "description": "output top" },
        { "name": "o3", "kind": "optical", "description": "output bottom" }
      ],
      "properties": { "passive": true, "reciprocal": true },
      "parameters": { "splitRatio": "50/50", "excessLoss_dB": 0.2 },
      "geometry": { "format": "none" },
      "artifacts": {
        "simulated": [
          {
            "file": "simulated/analytic-demo.json",
            "status": "demo",
            "provenance": {
              "method": "analytic-model",
              "tool": "generate_demo_data.py",
              "settings": "S21 = S31 = 10^(-0.2/20)/sqrt(2) * prop(5um)",
              "createdBy": "generate_demo_data.py",
              "date": "2026-07-06"
            }
          }
        ],
        "measured": []
      },
      "license": "MIT"
    }
    """;

    /// <summary>Repo-relative path of the Y-branch spectrum artifact.</summary>
    public const string YBranchSpectrumPath =
        "processes/generic-si220/components/y-branch-1x2/simulated/analytic-demo.json";

    /// <summary>Three-point S-parameter spectrum for the Y-branch.</summary>
    public const string YBranchSpectrumJson = """
    {
      "wavelength_um": [1.5, 1.55, 1.6],
      "s": [
        { "from": "o1", "to": "o2", "re": [0.5, -0.3, 0.1], "im": [0.2, 0.4, -0.6] },
        { "from": "o1", "to": "o3", "re": [0.5, -0.3, 0.1], "im": [0.2, 0.4, -0.6] },
        { "from": "o2", "to": "o1", "re": [0.5, -0.3, 0.1], "im": [0.2, 0.4, -0.6] },
        { "from": "o3", "to": "o1", "re": [0.5, -0.3, 0.1], "im": [0.2, 0.4, -0.6] }
      ]
    }
    """;
}
