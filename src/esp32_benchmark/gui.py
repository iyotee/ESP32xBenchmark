"""Windows-friendly Tkinter desktop application for ESP32xBenchmark."""

from __future__ import annotations

import json
import os
import queue
import shutil
import subprocess
import sys
import threading
import time
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, simpledialog, ttk
from typing import Any, Callable

from . import __version__
from .collector import collect_serial_runs
from .gui_backend import (
    HistoryStore,
    available_environments,
    calibrate_result,
    compare_and_write_reports,
    default_history_path,
    identify_board,
    list_serial_ports,
    load_profile,
    load_result,
    recommend_environment,
    score_and_write_reports,
)


ROOT_DIR = Path(__file__).resolve().parents[2]


def _default_project_dir() -> Path:
    if not getattr(sys, "frozen", False):
        return ROOT_DIR / "platformio_test"
    app_dir = Path(sys.executable).resolve().parent
    bundle_dir = Path(getattr(sys, "_MEIPASS", app_dir))
    for candidate in (
        bundle_dir / "platformio_test",
        app_dir / "platformio_test",
        app_dir,
        app_dir.parent / "platformio_test",
        Path.cwd() / "platformio_test",
        Path.cwd(),
    ):
        if (candidate / "platformio.ini").is_file():
            return candidate
    return app_dir / "platformio_test"


if getattr(sys, "frozen", False):
    _data_root = Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local"))
    DEFAULT_OUTPUT_DIR = _data_root / "ESP32xBenchmark" / "artifacts"
else:
    DEFAULT_OUTPUT_DIR = ROOT_DIR / "artifacts" / "desktop"
DEFAULT_PROJECT_DIR = _default_project_dir()


class BenchmarkApp:
    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.title(f"ESP32xBenchmark {__version__}")
        self.root.geometry("1220x820")
        self.root.minsize(1000, 680)
        self.root.configure(background="#101827")

        self.events: queue.Queue[tuple[str, Any]] = queue.Queue()
        self.busy = False
        self.current_result: dict[str, Any] | None = None
        self.detected_device: dict[str, Any] | None = None
        self.console_active = False
        self.console_connection: Any = None
        self.console_lock = threading.Lock()
        self.store = HistoryStore(default_history_path())

        self._configure_style()
        self._build_ui()
        self.refresh_ports()
        self.refresh_profiles()
        self.refresh_history()
        self.root.after(100, self._drain_events)
        self.root.protocol("WM_DELETE_WINDOW", self._close)

    def _configure_style(self) -> None:
        style = ttk.Style(self.root)
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        style.configure(".", font=("Segoe UI", 10))
        style.configure("TFrame", background="#101827")
        style.configure("Card.TFrame", background="#172338")
        style.configure("TLabel", background="#101827", foreground="#e6edf7")
        style.configure("Muted.TLabel", foreground="#9aaac0")
        style.configure("Title.TLabel", font=("Segoe UI Semibold", 20), foreground="#8ed8ff")
        style.configure("Section.TLabel", font=("Segoe UI Semibold", 12), foreground="#8ed8ff")
        style.configure("TNotebook", background="#101827", borderwidth=0)
        style.configure("TNotebook.Tab", padding=(16, 9), background="#172338", foreground="#d8e4f3")
        style.map("TNotebook.Tab", background=[("selected", "#24415d")])
        style.configure("Treeview", background="#172338", foreground="#e6edf7",
                        fieldbackground="#172338", rowheight=27, borderwidth=0)
        style.configure("Treeview.Heading", background="#24354c", foreground="#d9e8f8",
                        font=("Segoe UI Semibold", 9))
        style.map("Treeview", background=[("selected", "#275b7c")])
        style.configure("Accent.TButton", background="#087da7", foreground="white",
                        padding=(12, 7), font=("Segoe UI Semibold", 9))
        style.map("Accent.TButton", background=[("active", "#119bc9")])
        style.configure("TButton", padding=(9, 6))

    def _build_ui(self) -> None:
        shell = ttk.Frame(self.root, padding=(22, 16))
        shell.pack(fill="both", expand=True)
        header = ttk.Frame(shell)
        header.pack(fill="x", pady=(0, 14))
        ttk.Label(header, text="ESP32xBenchmark", style="Title.TLabel").pack(side="left")
        ttk.Label(header, text="  ·  Windows desktop", style="Muted.TLabel").pack(side="left", pady=(5, 0))
        self.status_var = tk.StringVar(value="Ready")
        ttk.Label(header, textvariable=self.status_var, style="Muted.TLabel").pack(side="right", pady=(7, 0))

        self.tabs = ttk.Notebook(shell)
        self.tabs.pack(fill="both", expand=True)
        self.run_tab = ttk.Frame(self.tabs, padding=16)
        self.console_tab = ttk.Frame(self.tabs, padding=16)
        self.history_tab = ttk.Frame(self.tabs, padding=16)
        self.tabs.add(self.run_tab, text="  Run benchmark  ")
        self.tabs.add(self.console_tab, text="  Serial console  ")
        self.tabs.add(self.history_tab, text="  History & compare  ")
        self._build_run_tab()
        self._build_console_tab()
        self._build_history_tab()

        footer = ttk.Frame(shell)
        footer.pack(fill="x", pady=(10, 0))
        self.log_box = tk.Text(footer, height=4, wrap="word", background="#0b1321",
                               foreground="#b7c8dc", insertbackground="white",
                               relief="flat", font=("Cascadia Mono", 9))
        self.log_box.pack(fill="x")
        self.log_box.configure(state="disabled")

    def _build_run_tab(self) -> None:
        pane = ttk.Frame(self.run_tab)
        pane.pack(fill="both", expand=True)
        controls = ttk.LabelFrame(pane, text="Device and capture setup", padding=14)
        controls.pack(fill="x")

        self.port_var = tk.StringVar()
        self.environment_var = tk.StringVar(value="esp32dev")
        self.baud_var = tk.StringVar(value="115200")
        self.runs_var = tk.StringVar(value="3")
        self.timeout_var = tk.StringVar(value="180")
        self.project_var = tk.StringVar(value=str(DEFAULT_PROJECT_DIR))
        self.output_var = tk.StringVar(value=str(DEFAULT_OUTPUT_DIR))
        self.profile_var = tk.StringVar(value="")
        self.upload_var = tk.BooleanVar(value=False)
        self.board_var = tk.StringVar(value="Board not identified — select a port and identify firmware.")

        ttk.Label(controls, text="Serial port").grid(row=0, column=0, sticky="w", padx=(0, 8), pady=5)
        self.port_combo = ttk.Combobox(controls, textvariable=self.port_var, width=17, state="normal")
        self.port_combo.grid(row=0, column=1, sticky="ew", pady=5)
        ttk.Button(controls, text="Refresh", command=self.refresh_ports).grid(row=0, column=2, padx=6)
        ttk.Button(controls, text="Identify board", command=self.identify_selected).grid(row=0, column=3, padx=6)
        ttk.Label(controls, text="Firmware environment").grid(row=1, column=0, sticky="w", padx=(0, 8), pady=5)
        self.environment_combo = ttk.Combobox(controls, textvariable=self.environment_var, width=25)
        self.environment_combo.grid(row=1, column=1, sticky="ew", pady=5)
        ttk.Label(controls, text="Reference profile").grid(row=1, column=2, sticky="e", padx=6)
        self.profile_combo = ttk.Combobox(controls, textvariable=self.profile_var, width=27, state="readonly")
        self.profile_combo.grid(row=1, column=3, sticky="ew", pady=5)
        ttk.Label(controls, text="Baud").grid(row=2, column=0, sticky="w", pady=5)
        ttk.Entry(controls, textvariable=self.baud_var, width=12).grid(row=2, column=1, sticky="w", pady=5)
        ttk.Label(controls, text="Complete runs").grid(row=2, column=2, sticky="e", padx=6)
        ttk.Spinbox(controls, textvariable=self.runs_var, from_=1, to=10, width=8).grid(row=2, column=3, sticky="w", pady=5)
        ttk.Label(controls, text="Timeout (seconds)").grid(row=3, column=0, sticky="w", pady=5)
        ttk.Entry(controls, textvariable=self.timeout_var, width=12).grid(row=3, column=1, sticky="w", pady=5)
        ttk.Label(controls, text="Project folder").grid(row=4, column=0, sticky="w", pady=5)
        ttk.Entry(controls, textvariable=self.project_var).grid(row=4, column=1, columnspan=2, sticky="ew", pady=5)
        ttk.Button(controls, text="Browse…", command=self.choose_project).grid(row=4, column=3, sticky="w", padx=6)
        ttk.Label(controls, text="Report folder").grid(row=5, column=0, sticky="w", pady=5)
        ttk.Entry(controls, textvariable=self.output_var).grid(row=5, column=1, columnspan=2, sticky="ew", pady=5)
        ttk.Button(controls, text="Browse…", command=self.choose_output).grid(row=5, column=3, sticky="w", padx=6)
        controls.columnconfigure(1, weight=1)
        controls.columnconfigure(3, weight=1)

        identity = ttk.Frame(pane, padding=(3, 10))
        identity.pack(fill="x")
        ttk.Label(identity, textvariable=self.board_var, style="Muted.TLabel").pack(side="left")
        ttk.Button(identity, text="Import profile…", command=self.import_profile).pack(side="right")

        actions = ttk.Frame(pane)
        actions.pack(fill="x", pady=(0, 10))
        ttk.Button(actions, text="Build firmware", command=self.build_firmware).pack(side="left", padx=(0, 7))
        ttk.Checkbutton(actions, text="Upload before capture", variable=self.upload_var).pack(side="left", padx=8)
        self.capture_button = ttk.Button(actions, text="Capture benchmark", style="Accent.TButton",
                                         command=self.capture_benchmark)
        self.capture_button.pack(side="left", padx=8)
        self.progress = ttk.Progressbar(actions, mode="indeterminate", length=150)
        self.progress.pack(side="right", padx=5)

        summary = ttk.Frame(pane)
        summary.pack(fill="x", pady=(0, 10))
        self.score_var = tk.StringVar(value="—")
        self.coverage_var = tk.StringVar(value="No scored capture yet")
        score_card = ttk.LabelFrame(summary, text="Global reference index", padding=(14, 8))
        score_card.pack(side="left", fill="y", padx=(0, 10))
        ttk.Label(score_card, textvariable=self.score_var, font=("Segoe UI Semibold", 25),
                  foreground="#71d5ff", background="#101827").pack(anchor="w")
        ttk.Label(score_card, textvariable=self.coverage_var, style="Muted.TLabel").pack(anchor="w")
        self.category_tree = self._tree(summary, ("category", "score", "weight"), (150, 100, 100), height=3)
        self.category_tree.pack(side="left", fill="x", expand=True)

        result_frame = ttk.LabelFrame(pane, text="Structured measurements", padding=8)
        result_frame.pack(fill="both", expand=True)
        self.metrics_tree = self._tree(result_frame,
                                       ("category", "metric", "value", "unit", "status", "variation"),
                                       (115, 270, 120, 85, 100, 110))
        self.metrics_tree.pack(fill="both", expand=True)
        self._set_environments()

    def _build_console_tab(self) -> None:
        toolbar = ttk.Frame(self.console_tab)
        toolbar.pack(fill="x", pady=(0, 10))
        ttk.Label(toolbar, text="Port").pack(side="left")
        self.console_port_var = tk.StringVar()
        self.console_port_combo = ttk.Combobox(toolbar, textvariable=self.console_port_var, width=18)
        self.console_port_combo.pack(side="left", padx=8)
        ttk.Button(toolbar, text="Refresh ports", command=self.refresh_ports).pack(side="left")
        ttk.Label(toolbar, text="Baud").pack(side="left", padx=(16, 4))
        self.console_baud_var = tk.StringVar(value="115200")
        ttk.Entry(toolbar, textvariable=self.console_baud_var, width=9).pack(side="left")
        self.connect_button = ttk.Button(toolbar, text="Connect", command=self.connect_console)
        self.connect_button.pack(side="left", padx=(12, 5))
        ttk.Button(toolbar, text="Disconnect", command=self.disconnect_console).pack(side="left")
        ttk.Label(toolbar, text="Opening the port may reset the board.", style="Muted.TLabel").pack(side="right")

        self.console_text = tk.Text(self.console_tab, background="#07101d", foreground="#bbd5e7",
                                    insertbackground="white", wrap="none", font=("Cascadia Mono", 10),
                                    relief="flat")
        yscroll = ttk.Scrollbar(self.console_tab, orient="vertical", command=self.console_text.yview)
        xscroll = ttk.Scrollbar(self.console_tab, orient="horizontal", command=self.console_text.xview)
        self.console_text.configure(yscrollcommand=yscroll.set, xscrollcommand=xscroll.set)
        self.console_text.pack(side="left", fill="both", expand=True)
        yscroll.pack(side="right", fill="y")
        xscroll.pack(side="bottom", fill="x")
        send_row = ttk.Frame(self.console_tab)
        send_row.pack(fill="x", pady=(10, 0))
        self.console_input = ttk.Entry(send_row)
        self.console_input.pack(side="left", fill="x", expand=True)
        self.console_input.bind("<Return>", lambda _event: self.send_console_line())
        ttk.Button(send_row, text="Send", command=self.send_console_line).pack(side="left", padx=(8, 0))

    def _build_history_tab(self) -> None:
        toolbar = ttk.Frame(self.history_tab)
        toolbar.pack(fill="x", pady=(0, 10))
        ttk.Button(toolbar, text="Refresh", command=self.refresh_history).pack(side="left")
        ttk.Button(toolbar, text="Import results…", command=self.import_results).pack(side="left", padx=6)
        ttk.Button(toolbar, text="Import reference…", command=self.import_profile).pack(side="left")
        ttk.Button(toolbar, text="Calibrate selected", command=self.calibrate_selected).pack(side="left", padx=6)
        ttk.Button(toolbar, text="Compare selected", style="Accent.TButton",
                   command=self.compare_selected).pack(side="left")
        ttk.Label(toolbar, text="Compare at least two rows using the selected reference on Run benchmark.",
                  style="Muted.TLabel").pack(side="right")
        frame = ttk.Frame(self.history_tab)
        frame.pack(fill="both", expand=True)
        self.history_tree = self._tree(frame,
            ("id", "target", "family", "class", "profile", "captured"),
            (60, 190, 110, 150, 170, 170), height=15)
        self.history_tree.pack(fill="both", expand=True)
        self.history_tree.bind("<Double-1>", lambda _event: self.show_history_result())
        ttk.Label(self.history_tab,
                  text="History is stored locally in your Windows user profile. Comparisons use only stable metrics common to every selected run.",
                  style="Muted.TLabel").pack(anchor="w", pady=(10, 0))

    @staticmethod
    def _tree(parent: tk.Misc, columns: tuple[str, ...], widths: tuple[int, ...],
              height: int = 8) -> ttk.Treeview:
        tree = ttk.Treeview(parent, columns=columns, show="headings", height=height,
                            selectmode="extended")
        for column, width in zip(columns, widths):
            tree.heading(column, text=column.replace("_", " ").title())
            tree.column(column, width=width, minwidth=55, stretch=column in {"metric", "target"})
        return tree

    def _set_environments(self) -> None:
        try:
            environments = available_environments(self.project_var.get())
        except OSError:
            environments = []
        self.environment_combo.configure(values=environments)
        if self.environment_var.get() not in environments and environments:
            self.environment_var.set(environments[0])

    def _log(self, message: str) -> None:
        self.log_box.configure(state="normal")
        self.log_box.insert("end", f"{time.strftime('%H:%M:%S')}  {message.rstrip()}\n")
        self.log_box.see("end")
        self.log_box.configure(state="disabled")

    def _console_log(self, message: str) -> None:
        self.console_text.insert("end", message)
        self.console_text.see("end")
        # Prevent an unattended console from growing without bound.
        if int(self.console_text.index("end-1c").split(".")[0]) > 5000:
            self.console_text.delete("1.0", "1000.0")

    def _set_busy(self, busy: bool, label: str = "Ready") -> None:
        self.busy = busy
        self.status_var.set(label)
        if busy:
            self.progress.start(12)
        else:
            self.progress.stop()

    def _drain_events(self) -> None:
        try:
            while True:
                kind, payload = self.events.get_nowait()
                if kind == "log":
                    self._log(str(payload))
                elif kind == "console":
                    self._console_log(str(payload))
                elif kind == "console_state":
                    self.connect_button.configure(state="normal")
                elif kind == "worker_done":
                    success, label, callback, value = payload
                    self._set_busy(False, "Ready")
                    if success:
                        self._log(f"{label} completed.")
                        if callback:
                            callback(value)
                    else:
                        self._log(f"{label} failed: {value}")
                        messagebox.showerror(label, str(value), parent=self.root)
        except queue.Empty:
            pass
        self.root.after(100, self._drain_events)

    def _start_worker(self, label: str, work: Callable[[], Any],
                      callback: Callable[[Any], None] | None = None) -> None:
        if self.busy:
            messagebox.showinfo("Please wait", "Another build or capture task is already running.",
                                parent=self.root)
            return
        self._set_busy(True, label)

        def run() -> None:
            try:
                result = work()
            except Exception as error:  # shown in the UI with actionable context
                self.events.put(("worker_done", (False, label, callback, error)))
            else:
                self.events.put(("worker_done", (True, label, callback, result)))

        threading.Thread(target=run, name=f"benchmark-{label.lower().replace(' ', '-')}",
                         daemon=True).start()

    def refresh_ports(self) -> None:
        try:
            ports = list_serial_ports()
        except RuntimeError as error:
            self.port_combo.configure(values=[])
            self.console_port_combo.configure(values=[])
            self._log(str(error))
            return
        values = [f"{item['port']} — {item['description']}" for item in ports]
        self._port_descriptions = {item["port"]: item["description"] for item in ports}
        self.port_combo.configure(values=values)
        self.console_port_combo.configure(values=values)
        if not values:
            self.port_var.set("")
            self.console_port_var.set("")
            self._log("No serial COM ports found. Connect a board and choose Refresh.")
            return
        for variable in (self.port_var, self.console_port_var):
            current = variable.get().split(" — ", 1)[0]
            if current not in [item["port"] for item in ports]:
                variable.set(values[0])

    def _selected_port(self, variable: tk.StringVar) -> str:
        return variable.get().split(" — ", 1)[0].strip()

    def identify_selected(self) -> None:
        if self.console_active or self.busy:
            messagebox.showwarning("Serial port in use", "Disconnect the serial console or finish the active task first.",
                                   parent=self.root)
            return
        port = self._selected_port(self.port_var)
        if not port:
            messagebox.showwarning("No COM port", "Connect a board and select a serial port first.",
                                   parent=self.root)
            return
        try:
            baud = int(self.baud_var.get())
        except ValueError:
            messagebox.showerror("Invalid baud rate", "Enter a valid integer baud rate.", parent=self.root)
            return

        def work() -> dict[str, Any]:
            return identify_board(port, baud, 7.0)

        def done(device: dict[str, Any]) -> None:
            self.detected_device = device
            environment, reason = recommend_environment(
                device, getattr(self, "_port_descriptions", {}).get(port, "")
            )
            text = f"{device.get('target', 'Unknown')} · {device.get('family', 'unknown')} · class {device.get('comparability_class', 'unknown')}"
            if environment:
                self.environment_var.set(environment)
                text += f"  |  Recommended: {environment}"
            self.board_var.set(text)
            self._log(reason)

        self._log(f"Listening for benchmark firmware identity on {port}…")
        self._start_worker("Board identification", work, done)

    def choose_project(self) -> None:
        selected = filedialog.askdirectory(initialdir=self.project_var.get(), title="Select PlatformIO project")
        if selected:
            self.project_var.set(selected)
            self._set_environments()

    def choose_output(self) -> None:
        selected = filedialog.askdirectory(initialdir=self.output_var.get(), title="Select report folder")
        if selected:
            self.output_var.set(selected)

    def _build_command(
        self, upload: bool, environment: str, upload_port: str = ""
    ) -> list[str]:
        pio = shutil.which("pio")
        if not pio:
            raise RuntimeError("PlatformIO CLI was not found. Install PlatformIO Core and ensure `pio` is on PATH.")
        env = environment.strip()
        if not env:
            raise ValueError("Choose a PlatformIO firmware environment.")
        command = [pio, "run", "-e", env]
        if upload:
            command.extend(["-t", "upload"])
            if upload_port:
                command.extend(["--upload-port", upload_port])
        return command

    def _run_platformio(
        self, upload: bool, project: Path, environment: str, upload_port: str = ""
    ) -> None:
        if not (project / "platformio.ini").is_file():
            raise FileNotFoundError(f"No platformio.ini was found in {project}.")
        command = self._build_command(upload, environment, upload_port)
        self.events.put(("log", f"Running {'upload' if upload else 'build'}: {' '.join(command)}"))
        process = subprocess.Popen(command, cwd=project, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, text=True, encoding="utf-8",
                                   errors="replace", bufsize=1)
        assert process.stdout is not None
        for line in process.stdout:
            self.events.put(("log", line.rstrip()))
        return_code = process.wait()
        if return_code:
            raise RuntimeError(f"PlatformIO exited with code {return_code}. See log for details.")

    def build_firmware(self) -> None:
        if self.console_active or self.busy:
            messagebox.showwarning("Serial port in use", "Disconnect the serial console before building.",
                                   parent=self.root)
            return
        project = Path(self.project_var.get())
        environment = self.environment_var.get()
        self._start_worker("PlatformIO build",
                           lambda: self._run_platformio(False, project, environment))

    def _capture(self, options: dict[str, Any]) -> dict[str, Any]:
        if self.console_active:
            raise RuntimeError("Disconnect the serial console before capturing; only one program can own a COM port.")
        port = options["port"]
        if not port:
            raise ValueError("No COM port is selected. Connect a supported board and refresh ports.")
        baud, runs, timeout = int(options["baud"]), int(options["runs"]), int(options["timeout"])
        if runs < 1 or timeout < 1:
            raise ValueError("Run count and timeout must be greater than zero.")
        if options["upload"]:
            self._run_platformio(
                True, options["project"], options["environment"], port
            )
        self.events.put(("log", f"Capturing {runs} complete run(s) from {port} at {baud} baud…"))
        result = collect_serial_runs(port, baud, timeout, runs)
        result["host"] = {
            "collector_version": __version__,
            "python_version": sys.version.split()[0],
            "platform": sys.platform,
            "platformio_environment": options["environment"],
            "serial_baud": baud,
        }
        profile_id = options["profile_id"]
        profile = self.store.get_profile(profile_id) if profile_id else None
        output_dir = options["output"] / "latest"
        score_and_write_reports(result, profile, output_dir)
        self.store.save_run(result, profile_id or "unscored")
        return result

    def capture_benchmark(self) -> None:
        if self.console_active:
            messagebox.showwarning("Serial port in use",
                "Disconnect the serial console before capture; only one program can own a COM port.",
                parent=self.root)
            return
        # Snapshot Tk variables on the UI thread; worker threads must not access Tcl state.
        options = {
            "port": self._selected_port(self.port_var),
            "baud": self.baud_var.get(),
            "runs": self.runs_var.get(),
            "timeout": self.timeout_var.get(),
            "upload": self.upload_var.get(),
            "project": Path(self.project_var.get()),
            "environment": self.environment_var.get(),
            "profile_id": self.profile_var.get(),
            "output": Path(self.output_var.get()),
        }

        def capture() -> dict[str, Any]:
            return self._capture(options)

        try:
            self._start_worker("Benchmark capture", capture, self._show_result)
        except (ValueError, RuntimeError) as error:
            messagebox.showerror("Cannot capture", str(error), parent=self.root)

    def _show_result(self, result: dict[str, Any]) -> None:
        self.current_result = result
        self._populate_result(result)
        device = result.get("device", {})
        self.board_var.set(f"{device.get('target', 'Unknown')} · {device.get('family', 'unknown')} · "
                           f"class {device.get('comparability_class', 'unknown')}")
        self.refresh_history()
        self._log(f"Captured {len(result.get('metrics', []))} metrics; reports saved under {Path(self.output_var.get()) / 'latest'}.")

    def _populate_result(self, result: dict[str, Any]) -> None:
        for tree in (self.metrics_tree, self.category_tree):
            for item in tree.get_children():
                tree.delete(item)
        scores = result.get("scores", {})
        score = scores.get("global_score")
        self.score_var.set("—" if score is None else f"{score:.2f}")
        self.coverage_var.set(
            f"{scores.get('coverage', 0):.0%} coverage · {scores.get('reference_profile', 'not scored')}"
            if scores else "Unscored · select/import a matching reference profile"
        )
        for category, data in scores.get("categories", {}).items():
            self.category_tree.insert("", "end", values=(category, f"{data['score']:.2f}", f"{data['weight']:.0%}"))
        metric_scores: dict[str, tuple[str, str]] = {}
        for category, data in scores.get("categories", {}).items():
            for item in data.get("metrics", []):
                metric_scores[item["name"]] = (category, f"{item['value']:.2f}")
        for metric in result.get("metrics", []):
            value = metric.get("value")
            value_text = "—" if value is None else f"{value:g}" if isinstance(value, (int, float)) else str(value)
            variation = metric.get("stdev_pct")
            variation_text = "—" if variation is None else f"{variation:.1f}%"
            scored = metric_scores.get(metric.get("name"))
            status = str(metric.get("status", "unknown"))
            if scored:
                status += f" · index {scored[1]}"
            self.metrics_tree.insert("", "end", values=(
                metric.get("category", ""), metric.get("name", ""), value_text,
                metric.get("unit", ""), status, variation_text,
            ))

    def connect_console(self) -> None:
        if self.busy:
            messagebox.showinfo("Please wait", "Disconnect or finish the current build/capture first.",
                                parent=self.root)
            return
        if self.console_active:
            return
        port = self._selected_port(self.console_port_var)
        if not port:
            messagebox.showwarning("No COM port", "Connect a board and select a serial port first.",
                                   parent=self.root)
            return
        try:
            baud = int(self.console_baud_var.get())
        except ValueError:
            messagebox.showerror("Invalid baud rate", "Enter a valid integer baud rate.", parent=self.root)
            return
        self.console_active = True
        self.connect_button.configure(state="disabled")
        self.events.put(("console", f"\n[Connecting to {port} at {baud} baud…]\n"))
        threading.Thread(target=self._console_reader, args=(port, baud),
                         daemon=True, name="serial-console").start()

    def _console_reader(self, port: str, baud: int) -> None:
        try:
            import serial
        except ImportError:
            self.events.put(("console", "[Serial support missing; install the hardware extra.]\n"))
            self.console_active = False
            self.events.put(("console_state", False))
            return
        try:
            connection = serial.Serial(port, baudrate=baud, timeout=0.25)
            with self.console_lock:
                self.console_connection = connection
            self.events.put(("console", f"[Connected to {port}. Opening the port may reset the board.]\n"))
            while self.console_active:
                raw = connection.readline()
                if raw:
                    self.events.put(("console", raw.decode("utf-8", errors="replace")))
            connection.close()
        except Exception as error:
            self.events.put(("console", f"\n[Serial error: {error}]\n"))
        finally:
            with self.console_lock:
                self.console_connection = None
            self.console_active = False
            self.events.put(("console_state", False))

    def disconnect_console(self) -> None:
        self.console_active = False
        with self.console_lock:
            connection = self.console_connection
        if connection is not None:
            try:
                connection.close()
            except Exception:
                pass
        self.connect_button.configure(state="normal")

    def send_console_line(self) -> None:
        message = self.console_input.get()
        if not message or not self.console_active:
            return
        try:
            with self.console_lock:
                connection = self.console_connection
                if connection is None or not connection.is_open:
                    raise RuntimeError("Serial console is not connected.")
                connection.write((message + "\n").encode("utf-8"))
            self._console_log(f"> {message}\n")
            self.console_input.delete(0, "end")
        except Exception as error:
            messagebox.showerror("Serial write failed", str(error), parent=self.root)

    def import_profile(self) -> None:
        filename = filedialog.askopenfilename(title="Open reference profile",
            filetypes=[("JSON files", "*.json"), ("All files", "*.*")])
        if not filename:
            return
        try:
            profile = load_profile(filename)
            self.store.save_profile(profile, filename)
        except (OSError, ValueError, json.JSONDecodeError) as error:
            messagebox.showerror("Could not import profile", str(error), parent=self.root)
            return
        self.refresh_profiles()
        self.profile_var.set(profile["profile_id"])
        self._log(f"Imported reference profile {profile['profile_id']} ({profile['comparability_class']}).")

    def refresh_profiles(self) -> None:
        profiles = self.store.list_profiles()
        self.profile_combo.configure(values=[""] + [item["profile_id"] for item in profiles])
        if self.profile_var.get() and self.profile_var.get() not in [item["profile_id"] for item in profiles]:
            self.profile_var.set("")

    def refresh_history(self) -> None:
        if not hasattr(self, "history_tree"):
            return
        for item in self.history_tree.get_children():
            self.history_tree.delete(item)
        for row in self.store.list_runs():
            self.history_tree.insert("", "end", iid=str(row["id"]), values=(
                row["id"], row["target"], row["family"], row["comparability_class"],
                row["profile_id"], row["captured_at"],
            ))

    def _selected_history_ids(self) -> list[int]:
        try:
            return [int(item) for item in self.history_tree.selection()]
        except ValueError:
            return []

    def show_history_result(self) -> None:
        selected = self._selected_history_ids()
        if not selected:
            return
        result = self.store.get_run(selected[0])
        profile_id = self.profile_var.get()
        if profile_id:
            try:
                from .scoring import score_result
                result["scores"] = score_result(result, self.store.get_profile(profile_id))
            except ValueError as error:
                messagebox.showwarning("Not scoreable with this profile", str(error), parent=self.root)
        self._populate_result(result)
        self.current_result = result
        self.tabs.select(self.run_tab)

    def import_results(self) -> None:
        filename = filedialog.askopenfilename(title="Open benchmark results",
            filetypes=[("JSON files", "*.json"), ("All files", "*.*")])
        if not filename:
            return
        try:
            result = load_result(filename)
            profile_id = self.profile_var.get()
            if profile_id:
                from .scoring import score_result
                result["scores"] = score_result(result, self.store.get_profile(profile_id))
            output_dir = Path(self.output_var.get()) / "imported"
            score_and_write_reports(result, None, output_dir)
            self.store.save_run(result, profile_id or "unscored")
        except (OSError, ValueError, KeyError, json.JSONDecodeError) as error:
            messagebox.showerror("Could not import results", str(error), parent=self.root)
            return
        self.refresh_history()
        self._show_result(result)

    def calibrate_selected(self) -> None:
        selected = self._selected_history_ids()
        if len(selected) != 1:
            messagebox.showinfo("Select one run", "Select exactly one history entry to create a reference profile.",
                                parent=self.root)
            return
        result = self.store.get_run(selected[0])
        suggested = str(result.get("device", {}).get("comparability_class", "benchmark-baseline"))
        suggested = suggested.replace(" ", "-") + "-baseline"
        profile_id = simpledialog.askstring("Create reference profile",
            "Profile ID (creates a measured calibration profile):", initialvalue=suggested, parent=self.root)
        if not profile_id or not profile_id.strip():
            return
        profile_id = profile_id.strip()
        if not profile_id[0].isalnum() or any(
            not (character.isalnum() or character in "._-") for character in profile_id
        ):
            messagebox.showerror("Invalid profile ID",
                "Use letters, numbers, dots, underscores, and hyphens; the first character must be alphanumeric.",
                parent=self.root)
            return
        try:
            profile = calibrate_result(result, profile_id)
            profile_dir = Path(self.output_var.get()) / "profiles"
            profile_dir.mkdir(parents=True, exist_ok=True)
            profile_path = profile_dir / f"{profile_id}.json"
            profile_path.write_text(json.dumps(profile, indent=2, allow_nan=False) + "\n", encoding="utf-8")
            self.store.save_profile(profile, profile_path)
        except (OSError, ValueError) as error:
            messagebox.showerror("Could not calibrate", str(error), parent=self.root)
            return
        self.refresh_profiles()
        self.profile_var.set(profile["profile_id"])
        self._log(f"Reference profile saved: {profile_path}")
        messagebox.showinfo("Reference profile created", f"Profile {profile['profile_id']} was saved to:\n{profile_path}",
                            parent=self.root)

    def compare_selected(self) -> None:
        selected = self._selected_history_ids()
        profile_id = self.profile_var.get()
        if len(selected) < 2:
            messagebox.showinfo("Select runs", "Select at least two board runs to compare.", parent=self.root)
            return
        if not profile_id:
            messagebox.showinfo("Select a reference", "Choose or import a shared reference profile on the Run benchmark tab.",
                                parent=self.root)
            return
        try:
            results = [self.store.get_run(run_id) for run_id in selected]
            profile = self.store.get_profile(profile_id)
        except (KeyError, ValueError) as error:
            messagebox.showerror("Cannot compare", str(error), parent=self.root)
            return
        output = Path(self.output_var.get()) / "comparison"

        def work() -> dict[str, Any]:
            return compare_and_write_reports(results, profile, output)

        def done(comparison: dict[str, Any]) -> None:
            lines = [
                f"{item['target']}  {item['global_score']:.2f}  "
                f"({item['comparability_class']})"
                for item in comparison["results"]
            ]
            self._log(f"Comparison reports saved to {output}")
            messagebox.showinfo("Shared-reference comparison",
                f"Profile: {comparison['reference_profile']}\n"
                f"Common stable metrics: {len(comparison['common_metrics'])}\n"
                f"Common coverage: {comparison['coverage']:.0%}\n\n" + "\n".join(lines),
                parent=self.root)

        self._start_worker("Board comparison", work, done)

    def _close(self) -> None:
        self.disconnect_console()
        self.root.destroy()


def main() -> None:
    try:
        root = tk.Tk()
    except tk.TclError as error:
        raise SystemExit(f"Could not start the desktop UI: {error}") from error
    BenchmarkApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
