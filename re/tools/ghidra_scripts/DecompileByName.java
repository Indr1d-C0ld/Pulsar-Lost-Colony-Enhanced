// Decompila le funzioni già nominate il cui nome corrisponde a una regex. Argomenti: <outdir> <regex>
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import java.io.*;
import java.util.regex.*;

public class DecompileByName extends GhidraScript {
    public void run() throws Exception {
        String[] a = getScriptArgs();
        Pattern pat = Pattern.compile(a[1]);
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        int n = 0;
        for (Function f : currentProgram.getFunctionManager().getFunctions(true)) {
            if (!pat.matcher(f.getName()).find()) continue;
            DecompileResults res = di.decompileFunction(f, 180, monitor);
            String name = f.getName().replaceAll("[^A-Za-z0-9_.]", "_") + "_" + f.getEntryPoint() + ".c";
            try (PrintWriter w = new PrintWriter(new File(a[0], name))) {
                w.println(res.decompileCompleted() ? res.getDecompiledFunction().getC() : "// FALLITA: " + res.getErrorMessage());
            }
            n++;
        }
        println("decompilate: " + n);
        di.dispose();
    }
}
