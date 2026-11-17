using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace RamOptimiser.App
{
    /// <summary>
    /// A card read by screen readers as one piece of text: its AutomationProperties.Name, with the visual
    /// parts inside hidden, so "89", "%", "memory load" and "13.7 GB of 15.2 GB in use" are heard together.
    /// </summary>
    public class SummaryPanel : Border
    {
        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new SummaryPanelPeer(this);
        }

        sealed class SummaryPanelPeer : FrameworkElementAutomationPeer
        {
            public SummaryPanelPeer(FrameworkElement owner) : base(owner) { }
            protected override AutomationControlType GetAutomationControlTypeCore() { return AutomationControlType.Text; }
            protected override string GetClassNameCore() { return "SummaryPanel"; }
            protected override List<AutomationPeer> GetChildrenCore() { return null; }
            protected override bool IsControlElementCore() { return true; }
            protected override bool IsContentElementCore() { return true; }
        }
    }

    /// <summary>
    /// A group whose changes are announced politely (AutomationProperties.LiveSetting, .NET Framework 4.7.1+).
    /// Call Announce after updating its Name and content.
    /// </summary>
    public class LiveRegion : Border
    {
        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new LiveRegionPeer(this);
        }

        public void Announce()
        {
            if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
            AutomationPeer peer = UIElementAutomationPeer.FromElement(this) ?? UIElementAutomationPeer.CreatePeerForElement(this);
            if (peer != null) peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        sealed class LiveRegionPeer : FrameworkElementAutomationPeer
        {
            public LiveRegionPeer(FrameworkElement owner) : base(owner) { }
            protected override AutomationControlType GetAutomationControlTypeCore() { return AutomationControlType.Group; }
            protected override string GetClassNameCore() { return "LiveRegion"; }
            protected override bool IsControlElementCore() { return true; }
        }
    }
}
