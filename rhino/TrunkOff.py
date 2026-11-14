#! python3
# Rhino alias:  TrunkOff   ! _-RunPythonScript "C:\Users\rober\repos\git_engine\rhino\TrunkOff.py"
import os
RT_CMD = "off"
RT_HOME = os.path.dirname(os.path.abspath(__file__)) if "__file__" in globals() else r"C:\Users\rober\repos\git_engine\rhino"
exec(open(os.path.join(RT_HOME, "rhinotrunk.py"), encoding="utf-8").read(), globals())
