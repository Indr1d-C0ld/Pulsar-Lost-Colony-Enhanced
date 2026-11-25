// Applica i nomi IL2CPP (method_addrs.tsv) e decompila i metodi che corrispondono a una regex.
// Argomenti: <method_addrs.tsv> <outdir> <regex su Classe::Metodo>
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;
import java.util.regex.*;

public class Il2CppLabelDecompile extends GhidraScript {
    public void run() throws Exception {
        String[] a = getScriptArgs();
        Pattern pat = Pattern.compile(a[2]);
        List<String[]> rows = new ArrayList<>();
        try (BufferedReader r = new BufferedReader(new FileReader(a[0]))) {
            String l; while ((l = r.readLine()) != null) rows.add(l.split("\t"));
        }
        FunctionManager fm = currentProgram.getFunctionManager();
        int n = 0;
        List<Function> targets = new ArrayList<>();
        for (String[] row : rows) {
            Address ad = toAddr(Long.parseUnsignedLong(row[0].substring(2), 16));
            String nm = (row[2] + "::" + row[3]).replaceAll("[^A-Za-z0-9_:.<>]", "_");
            if (getInstructionAt(ad) == null) disassemble(ad);
            Function f = fm.getFunctionAt(ad);
            if (f == null) f = createFunction(ad, nm);
            if (f == null) { createLabel(ad, nm, true); continue; }
            if (f.getSymbol().getSource() == SourceType.DEFAULT || f.getName().startsWith("FUN_"))
                f.setName(nm, SourceType.IMPORTED);
            n++;
            if (pat.matcher(row[2] + "::" + row[3]).find()) targets.add(f);
        }
        println("funzioni nominate: " + n + ", da decompilare: " + targets.size());
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        for (Function f : targets) {
            DecompileResults res = di.decompileFunction(f, 120, monitor);
            String name = f.getName().replaceAll("[^A-Za-z0-9_.]", "_") + "_" + f.getEntryPoint() + ".c";
            try (PrintWriter w = new PrintWriter(new File(a[1], name))) {
                w.println(res.decompileCompleted() ? res.getDecompiledFunction().getC() : "// FALLITA: " + res.getErrorMessage());
            }
        }
        di.dispose();
    }
}
