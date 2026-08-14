namespace CAP_Core.Components.Core;

/// <summary>
/// Slider management of <see cref="Component"/>: the slider map, the change
/// propagation into the S-matrices' slider references, and slider cloning.
/// Split out to keep Component.cs under the project's 500-line gate.
/// </summary>
public partial class Component
{
    private Dictionary<int, Slider> SliderMap { get; set; } // where int is the sliderNumber
    public event EventHandler SliderValueChanged;

    // adds the slider to the component and its SMatrices
    public void AddSlider(int sliderNr , Slider slider)
    {
        if(SliderMap.TryAdd(sliderNr, slider))
        {
            slider.PropertyChanged += Slider_PropertyChanged;
        }
        SliderMap[slider.Number].Value = slider.Value;
        foreach(int waveLength in WaveLengthToSMatrixMap.Keys)
        {
            if  (WaveLengthToSMatrixMap[waveLength].SliderReference.ContainsKey(slider.ID) == false) {
                WaveLengthToSMatrixMap[waveLength].SliderReference.Add(slider.ID, slider.Value);
            } else
            {
                WaveLengthToSMatrixMap[waveLength].SliderReference[slider.ID] = slider.Value;
            }

        }
    }

    private void Slider_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if(e.PropertyName == nameof(Slider.Value) && sender is Slider slider)
        {
            foreach (var sMatrix in WaveLengthToSMatrixMap.Values)
            {
                if (sMatrix.SliderReference.ContainsKey(slider.ID))
                {
                    sMatrix.SliderReference[slider.ID] = slider.Value;
                }
                else
                {
                    sMatrix.SliderReference.Add(slider.ID, slider.Value);
                }
            }
            SliderValueChanged?.Invoke(sender, e);
            // Note: Slider values are for simulation only. GDS export stubs don't support parameters.
            // NazcaFunctionParameters should only be set from PDK metadata, not from sliders.
        }
    }

    /// <summary>
    /// Retrieves slider by its index (there can be multiple sliders on a single component)
    /// </summary>
    /// <param name="sliderNr">index of a slider (starts from 0)</param>
    /// <returns><see cref="Slider"/> of the component at the given index, or null if it doesn't exist</returns>
    public Slider? GetSlider (int sliderNr)
    {
        if(SliderMap.TryGetValue(sliderNr, out Slider? slider) == true)
        {
            return slider;
        }
        return null;
    }
    public List<Slider> GetAllSliders()
    {
        return SliderMap.Values.ToList();
    }

    private Dictionary<int, Slider> CloneSliders()
    {
        var clonedSliderMap = new Dictionary<int, Slider>();
        foreach (var sliderID in SliderMap.Keys)
        {
            var slider = SliderMap[sliderID];
            var clonedSlider = (Slider)slider.Clone();
            clonedSlider.ID = Guid.NewGuid();
            clonedSlider.Value = slider.Value;
            clonedSliderMap.Add(slider.Number, clonedSlider);
        }

        return clonedSliderMap;
    }
}
