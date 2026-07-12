"""DeskFader CircuitPython firmware: protocol v1 and one motorized fader."""
import analogio
import board
import digitalio
import json
import time
import touchio
import usb_cdc

SLOT_COUNT = 6
PROTOCOL_VERSION = 1
MAX_FRAME_BYTES = 256
ADC_MIN = 2620          # Calibrate to the physical low stop.
ADC_MAX = 62500         # Calibrate to the physical high stop.
DEADBAND = 2            # Percent points.
MOTOR_TIMEOUT = 1.5     # Seconds of continuous drive.
BUTTON_DEBOUNCE = 0.04
REPORT_INTERVAL = 0.08

serial = usb_cdc.data
fader = analogio.AnalogIn(board.A0)
touch = touchio.TouchIn(board.A3)
motor_fwd = digitalio.DigitalInOut(board.SDA)
motor_bwd = digitalio.DigitalInOut(board.SCL)
for motor in (motor_fwd, motor_bwd):
    motor.direction = digitalio.Direction.OUTPUT
    motor.value = False

buttons = [digitalio.DigitalInOut(pin) for pin in
           (board.D5, board.D6, board.D9, board.D10, board.D11, board.D12)]
leds = [digitalio.DigitalInOut(pin) for pin in
        (board.D4, board.TX, board.RX, board.MISO, board.MOSI, board.SCK)]
for button in buttons:
    button.direction = digitalio.Direction.INPUT
    button.pull = digitalio.Pull.UP
for led in leds:
    led.direction = digitalio.Direction.OUTPUT
    led.value = False

volumes = [40, 100, 20, 50, 50, 50]
current_slot = 0
leds[current_slot].value = True
device_seq = 0
line_buffer = bytearray()
discarding_frame = False
button_state = [True] * SLOT_COUNT
button_changed = [0.0] * SLOT_COUNT
button_selectable = [False] * SLOT_COUNT
motor_started = None
motor_lockout_until = 0.0
last_report = 0.0
last_hello = 0.0
synced = False
touch_active = False
manual_position = None
serial_connected = serial.connected

if serial_connected:
    print("Pairing with host")
else:
    print("Waiting for host")


def send(message):
    try:
        payload = json.dumps(message, separators=(",", ":")).encode("utf-8")
        if len(payload) <= MAX_FRAME_BYTES and serial.connected:
            serial.write(payload + b"\n")
    except (ValueError, OSError):
        pass


def error(code, message):
    send({"type": "error", "code": code, "message": message})


def is_valid_state(message):
    return (
        message.get("type") == "state"
        and set(message) == {"type", "seq", "volumes"}
        and isinstance(message["seq"], int)
        and not isinstance(message["seq"], bool)
        and 0 <= message["seq"] <= 2147483647
        and isinstance(message["volumes"], list)
        and len(message["volumes"]) == SLOT_COUNT
        and all(isinstance(value, int) and not isinstance(value, bool) and 0 <= value <= 100
                for value in message["volumes"])
    )


def position():
    scaled = (fader.value - ADC_MIN) * 100 // (ADC_MAX - ADC_MIN)
    return max(0, min(100, scaled))


def stop_motor():
    global motor_started
    motor_fwd.value = False
    motor_bwd.value = False
    motor_started = None


def drive_motor(target, now):
    global motor_started, motor_lockout_until
    if not (serial.connected and synced) or touch.value:
        stop_motor()
        return
    delta = target - position()
    if abs(delta) <= DEADBAND:
        stop_motor()
        return
    if now < motor_lockout_until:
        return
    if motor_started is not None and now - motor_started >= MOTOR_TIMEOUT:
        stop_motor()
        motor_lockout_until = now + 0.5
        return
    if motor_started is None:
        motor_started = now
    motor_fwd.value = delta > 0
    motor_bwd.value = delta < 0


def receive():
    global device_seq, discarding_frame, line_buffer, synced
    if not serial.connected or not serial.in_waiting:
        return
    line_buffer.extend(serial.read(min(serial.in_waiting, 64)))
    while b"\n" in line_buffer:
        raw, _, line_buffer = line_buffer.partition(b"\n")
        if discarding_frame:
            discarding_frame = False
            continue
        if len(raw) > MAX_FRAME_BYTES:
            error("frame_too_large", "maximum frame size exceeded")
            continue
        try:
            message = json.loads(raw.decode("utf-8").strip())
            if not isinstance(message, dict):
                raise ValueError()
        except (UnicodeError, ValueError):
            error("invalid_json", "expected JSON object")
            continue
        if is_valid_state(message):
            volumes[:] = message["volumes"]
            if not synced:
                print("Paired with host")
            synced = True
            stop_motor()
            send({"type": "ack", "seq": message["seq"]})
            # A new desktop host can reconnect without USB CDC disconnecting.
            device_seq = (device_seq + 1) % 2147483648
            send({"type": "select", "seq": device_seq, "slot": current_slot})
        else:
            error("invalid_message", "expected state")
    if not discarding_frame and len(line_buffer) > MAX_FRAME_BYTES:
        line_buffer = bytearray()
        discarding_frame = True
        error("frame_too_large", "maximum frame size exceeded")


def check_buttons(now):
    global current_slot, device_seq, manual_position
    for index, button in enumerate(buttons):
        pressed = not button.value  # Buttons are active-low with pull-ups.
        if pressed != button_state[index]:
            button_state[index] = pressed
            button_changed[index] = now
            button_selectable[index] = pressed and serial.connected and synced
        elif (pressed and button_selectable[index] and serial.connected and synced
              and now - button_changed[index] >= BUTTON_DEBOUNCE):
            if index != current_slot:
                leds[current_slot].value = False
                current_slot = index
                leds[current_slot].value = True
            button_changed[index] = now + 3600  # One selection per press.
            button_selectable[index] = False
            stop_motor()
            if touch_active:
                manual_position = position()
            device_seq = (device_seq + 1) % 2147483648
            send({"type": "select", "seq": device_seq, "slot": current_slot})
            print("Selected slot %d" % (current_slot + 1))


def check_connection():
    global serial_connected
    connected = serial.connected
    if connected == serial_connected:
        return connected
    serial_connected = connected
    if connected:
        print("Pairing with host")
    else:
        for index in range(SLOT_COUNT):
            button_selectable[index] = False
        print("Host disconnected")
        print("Waiting for host")
    return connected


def check_touch():
    global manual_position, touch_active
    touched = touch.value
    if touched == touch_active:
        return touched
    touch_active = touched
    if touched:
        stop_motor()
        print("Touch start")
    else:
        manual_position = None
        print("Touch release")
    return touched


while True:
    now = time.monotonic()
    connected = check_connection()
    check_touch()
    if not connected:
        synced = False
        line_buffer = bytearray()
        discarding_frame = False
        manual_position = None
        stop_motor()
    receive()
    if connected and now - last_hello >= 1.0:
        send({"type": "hello", "version": PROTOCOL_VERSION})
        last_hello = now
    check_buttons(now)
    if connected and synced:
        drive_motor(volumes[current_slot], now)
    else:
        stop_motor()
    if connected and synced and touch_active and now - last_report >= REPORT_INTERVAL:
        value = position()
        if manual_position is None or abs(value - manual_position) > DEADBAND:
            manual_position = None
            if value != volumes[current_slot]:
                volumes[current_slot] = value
                device_seq = (device_seq + 1) % 2147483648
                send({"type": "volume", "seq": device_seq, "slot": current_slot, "value": value})
                print("Volume adjusted: slot %d to %d" % (current_slot + 1, value))
        last_report = now
    time.sleep(0.002)
