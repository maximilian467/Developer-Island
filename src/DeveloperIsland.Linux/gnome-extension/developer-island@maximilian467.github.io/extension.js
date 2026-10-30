// Developer Island helper for GNOME Shell.
//
// On a Wayland session, only the compositor knows which window is in front. This extension shares
// exactly that with Developer Island over the session bus: the focused window's app id and process
// id, whether it is maximized or fullscreen, and its frame rectangle (to know its monitor). It sends
// a signal when focus or that window's state changes; nothing polls, nothing else is exposed.

import Gio from 'gi://Gio';
import Meta from 'gi://Meta';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

const BUS_NAME = 'io.github.DeveloperIsland.Shell';
const OBJECT_PATH = '/io/github/DeveloperIsland/Shell';
const VERSION = 1;

const INTERFACE = `
<node>
  <interface name="io.github.DeveloperIsland.Shell1">
    <method name="GetFocus">
      <arg direction="out" type="s" name="app" />
      <arg direction="out" type="i" name="pid" />
      <arg direction="out" type="b" name="maximized" />
      <arg direction="out" type="b" name="fullscreen" />
      <arg direction="out" type="i" name="x" />
      <arg direction="out" type="i" name="y" />
      <arg direction="out" type="i" name="width" />
      <arg direction="out" type="i" name="height" />
    </method>
    <signal name="FocusChanged" />
    <property name="Version" type="u" access="read" />
  </interface>
</node>`;

export default class DeveloperIslandHelper extends Extension {
    enable() {
        this._service = Gio.DBusExportedObject.wrapJSObject(INTERFACE, this);
        this._service.export(Gio.DBus.session, OBJECT_PATH);
        this._nameId = Gio.bus_own_name_on_connection(Gio.DBus.session, BUS_NAME, Gio.BusNameOwnerFlags.NONE, null, null);
        this._window = null;
        this._windowSignals = [];
        this._focusId = global.display.connect('notify::focus-window', () => this._onFocusChanged());
        this._onFocusChanged();
    }

    disable() {
        global.display.disconnect(this._focusId);
        this._focusId = 0;
        this._unwatchWindow();
        Gio.bus_unown_name(this._nameId);
        this._nameId = 0;
        this._service.unexport();
        this._service = null;
    }

    get Version() {
        return VERSION;
    }

    GetFocus() {
        const w = global.display.focus_window;
        if (!w) {
            return ['', 0, false, false, 0, 0, 0, 0];
        }

        const rect = w.get_frame_rect();
        const app = w.get_wm_class() || w.get_gtk_application_id() || '';
        return [app, w.get_pid(), isMaximized(w), w.is_fullscreen(), rect.x, rect.y, rect.width, rect.height];
    }

    _onFocusChanged() {
        this._unwatchWindow();
        const w = global.display.focus_window;
        if (w) {
            this._window = w;
            for (const signal of ['notify::maximized-horizontally', 'notify::maximized-vertically', 'notify::fullscreen', 'workspace-changed']) {
                try {
                    this._windowSignals.push(w.connect(signal, () => this._emit()));
                } catch {
                    // A signal this GNOME version does not have.
                }
            }
        }

        this._emit();
    }

    _unwatchWindow() {
        if (this._window) {
            for (const id of this._windowSignals) {
                try {
                    this._window.disconnect(id);
                } catch {
                    // The window is gone.
                }
            }
        }

        this._window = null;
        this._windowSignals = [];
    }

    _emit() {
        this._service?.emit_signal('FocusChanged', null);
    }
}

/** Both directions maximized; GNOME 49 renamed the call. */
function isMaximized(w) {
    if (typeof w.is_maximized === 'function') {
        return w.is_maximized();
    }

    return w.get_maximized() === Meta.MaximizeFlags.BOTH;
}
