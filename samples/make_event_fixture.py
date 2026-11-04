#!/usr/bin/env python3
"""Writes samples/event-fixture: a small Zimension export (masterData.json, Basics shapes only, no
binary assets) exercising the event workflow end to end. Import the folder (or a zip of it) with
Zimension > Import from Zimension (tab "From a file") in Unity, then press Play.

Walkthrough: walk onto the gold coin (auto pickup: coins + 1, score + 10), walk to the red button
and press F (message, then the door rises 1 s later), when the door has finished opening a
condition checks coins >= 1 and shows the reward sphere and an overlay; otherwise it says you
need the coin. A platform ping-pongs on its own and a cube spins (procedural animation)."""
import json, math, os

def matrix(x=0.0, y=0.0, z=0.0, sx=1.0, sy=1.0, sz=1.0, yaw=0.0):
    """three.js Matrix4.elements (column-major) for scale, Y rotation and translation."""
    c, s = math.cos(yaw), math.sin(yaw)
    return [c * sx, 0, -s * sx, 0,  0, sy, 0, 0,  s * sz, 0, c * sz, 0,  x, y, z, 1]

def shape(_id, name, shape_name, dims, color, transform, **extra):
    item = {"_id": _id, "name": name, "type": "primitive", "sceneId": "scene-1",
            "primitiveSettings": {"shape": shape_name, "dimensions": dims, "color": color,
                                  "roughness": 0.6, "metalness": 0.05, "collision": "object"},
            "transformation": transform, "collision": "object"}
    item.update(extra)
    return item

events = ["intro", "button_pressed", "open_door", "door_opened", "reward_show", "need_coin", "coin_collected"]
world = {
    "_id": "fixture-world", "name": "Event Fixture", "showGround": True, "size": 60,
    "sunPosition": {"x": 0.35, "y": 0.25}, "sunIntensity": 3, "sunColor": "#fff4e0",
    "spawnPosition": {"x": 0, "y": 0, "z": 8},
    "avatar": {"cameraType": "TPP", "cameraDistance": 3.5, "cameraHeight": 1.7, "walkSpeed": 1, "sprintSpeed": 1, "jumpHeight": 1,
               "rotation": {"x": 0, "y": 0, "z": 0}},
    "events": events,
    "variables": [{"name": "coins", "dataType": "number", "initialValue": 0},
                  {"name": "score", "dataType": "number", "initialValue": 0}],
    "eventBroadcasters": [
        {"name": "Intro", "startEvent": "on_start", "actionIndex": 0, "actionType": "Trigger Events", "triggerEvents": ["intro"],
         "showMessage": "Grab the gold coin, then press F at the red button.", "link": ""},
        {"name": "Button", "startEvent": "button_pressed", "actionIndex": 0, "actionType": "Trigger Events", "triggerEvents": ["open_door"],
         "delay": 1, "showMessage": "The door opens in one second...", "link": ""},
        {"name": "Check coin", "startEvent": "door_opened", "actionIndex": 2, "actionType": "Check Condition", "trueEvent": "reward_show", "falseEvent": "need_coin",
         "conditions": [{"targetVariable": {"name": "coins"}, "logicOperator": ">=", "variableValueMode": "Value", "variableValue": "1"}]},
        {"name": "Need coin", "startEvent": "need_coin", "actionIndex": 0, "actionType": "Trigger Events", "triggerEvents": [],
         "showMessage": "You need the coin first! Collect it and press the button again.", "link": ""},
        {"name": "Score", "startEvent": "coin_collected", "actionIndex": 1, "actionType": "Change Variable",
         "targetVariable": {"name": "score", "dataType": "number"}, "values": [{"type": "Value", "value": "10"}], "arthematicOperators": ["+"]},
    ],
    "customHUDUI": {"elements": [
        {"id": "coins", "type": "text-box", "valueType": "variable", "variableName": "coins", "prefix": "Coins: ", "text": "Coins: 0",
         "style": {"left": "20px", "top": "20px", "fontSize": "20px", "backgroundColor": "rgba(0,0,0,0.55)", "color": "#ffffff"}},
        {"id": "score", "type": "text-box", "valueType": "variable", "variableName": "score", "prefix": "Score: ", "text": "Score: 0",
         "style": {"left": "20px", "top": "56px", "fontSize": "20px", "backgroundColor": "rgba(0,0,0,0.55)", "color": "#ffd34d"}},
    ]},
    "customUIOverRelays": [
        {"event": "reward_show", "hideEventName": "", "closeOnClick": True, "containerStyle": {"backgroundColor": "rgba(0,0,0,0.4)"},
         "elements": [
             {"id": "title", "type": "text-box", "text": "You found the reward!",
              "style": {"left": "50%", "top": "40%", "transform": "translate(-50%, -50%)", "fontSize": "32px", "backgroundColor": "rgba(20,40,80,0.85)", "color": "#ffffff"}},
             {"id": "ok", "type": "button", "text": "Continue", "closesOverlay": True,
              "style": {"left": "50%", "top": "55%", "transform": "translate(-50%, -50%)"}}]}],
    "nodes": [{"id": "scene-1", "type": "scene", "name": "Courtyard", "isBaseScene": True, "children": [], "settings": {"worldSettings": {}}}],
    "baseSceneId": "scene-1",
}

items = [
    shape("wall-left", "Wall left", "box", {"width": 4, "height": 3, "depth": 0.4}, "#8a8f99", matrix(-3, 1.5, -4)),
    shape("wall-right", "Wall right", "box", {"width": 4, "height": 3, "depth": 0.4}, "#8a8f99", matrix(3, 1.5, -4)),
    shape("door", "Door", "box", {"width": 2, "height": 3, "depth": 0.3}, "#6b4a2f", matrix(0, 1.5, -4),
          states=[{"transformation": matrix(0, 4.6, -4)}],
          transitions=[{"name": "Open", "playmode": "event", "event": "open_door", "loop": False, "reset": False,
                        "transitionFinishedEvent": "door_opened", "transitionStopEvent": "",
                        "steps": [{"stateIndex": 0, "duration": 1, "movement": "Smooth"}, {"stateIndex": 1, "duration": 1, "movement": "Smooth"}]}]),
    shape("button", "Red button", "cylinder", {"radius": 0.35, "height": 1.0}, "#d23b2f", matrix(3, 0.5, 2),
          enableInteraction=True, interactionName="press the button", interactionTriggers=["button_pressed"], interactionTime=2),
    shape("coin", "Gold coin", "cylinder", {"radius": 0.4, "height": 0.08}, "#f2c230", matrix(-3, 1.0, 3),
          collectable=True, collectSettings={"autoPickup": True}, collectCountVariable={"name": "coins", "dataType": "number"},
          onCollectEvent="coin_collected", allowAnimations=True,
          animations=[{"playmode": "auto", "animationSource": "default", "proceduralSettings": {"type": "spin", "axis": "y", "speed": 0.5, "intensity": 1}}]),
    shape("reward", "Reward", "sphere", {"radius": 0.6}, "#38c172", matrix(0, 1.0, -7), spawnType="event", spawnEvent="reward_show"),
    shape("platform", "Moving platform", "box", {"width": 2, "height": 0.3, "depth": 2}, "#4a7bd0", matrix(6, 0.15, -2),
          states=[{"transformation": matrix(6, 0.15, 4)}],
          transitions=[{"name": "Ping-pong", "playmode": "auto", "event": "", "loop": True, "reset": True,
                        "steps": [{"stateIndex": 0, "duration": 3, "movement": "Smooth"}, {"stateIndex": 1, "duration": 3, "movement": "Smooth"}]}]),
    shape("spinner", "Spinning cube", "box", {"width": 0.8, "height": 0.8, "depth": 0.8}, "#b05bd6", matrix(-6, 1.2, -2),
          allowAnimations=True,
          animations=[{"playmode": "auto", "animationSource": "default", "proceduralSettings": {"type": "spin", "axis": "y", "speed": 0.2, "intensity": 1}}]),
]

root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "event-fixture", "static", "files")
os.makedirs(root, exist_ok=True)
with open(os.path.join(root, "masterData.json"), "w") as f:
    json.dump({"world": world, "worldItems": items}, f, indent=2)
print("wrote", os.path.join(root, "masterData.json"))
