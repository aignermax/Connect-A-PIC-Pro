using CAP_Core.Tiles;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CAP_Core.Components.Core
{
    public class Pin : ICloneable
    {
        public string Name { get; set; } // the nazca name like b0, a0, a1}
        public int PinNumber { get; private set; }// the number specified in the draft 
        public Guid IDInFlow { get; set; } // each pin is divided into two IDs -> inflow and outflow ID.
        public Guid IDOutFlow { get; set; }
        [JsonIgnore] public Complex LightInflow { get; set; } // the light flowing into this component at this pin
        [JsonIgnore] public Complex LightOutflow { get; set; } // the light exiting this component at this pin
        private RectSide _side;
        public RectSide Side
        {
            get => _side;
            private set
            {
                _side = value;
            }
        }
        private MatterType _matterType;
        public MatterType MatterType
        {
            get => _matterType;
            set
            {
                SetMatterType(value);
            }
        }

        /// <summary>
        /// Polarization mode of the light carried by this pin. Defaults to
        /// <see cref="PolarizationKind.TE"/> — the historical implicit kind —
        /// so old PDKs and designs keep their exact behaviour.
        /// </summary>
        public PolarizationKind Polarization { get; set; } = PolarizationKind.TE;
        public Pin(string Name, int pinNumber, MatterType newMatterType, RectSide side, Guid idInFlow, Guid idOutFlow) : this(Name, pinNumber,newMatterType, side)
        {
            IDInFlow = idInFlow;
            IDOutFlow = idOutFlow;
        }
        public Pin(string Name, int pinNumber, MatterType newMatterType, RectSide side) : this(Name, pinNumber, side)
        {
            MatterType = newMatterType;
        }
        public Pin(string Name, int pinNumber, RectSide side)
        {
            Side = side;
            this.Name = Name;
            this.PinNumber = pinNumber;
            MatterType = MatterType.Light;
            IDInFlow = Guid.NewGuid();
            IDOutFlow = Guid.NewGuid();
        }

        public void SetMatterType(MatterType newMatterType)
        {
            _matterType = newMatterType;
        }
        public void Reset()
        {
            SetMatterType(MatterType.None);
            Name = "";
        }

        public override string ToString()
        {
            return $"{Name}: {MatterType}";
        }

        public object Clone()
        {
            var clonedPin = new Pin(Name, PinNumber, MatterType, Side, IDInFlow , IDOutFlow)
            {
                Polarization = Polarization
            };
            return clonedPin;
        }
    }
}