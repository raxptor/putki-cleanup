package putki;

import java.io.IOException;
import java.nio.file.*;
import java.util.*;

public class CodeWriter
{
	HashMap<Path, byte[]> m_results;

	public CodeWriter()
	{
		m_results = new HashMap<>();
	}

	public void addOutput(Path p, byte[] blob)
	{
		System.out.println("adding [" + p.toAbsolutePath() + "] " + blob.length);
		m_results.put(p,  blob);
	}

	public void write()
	{
		for (Map.Entry<Path, byte[]> entry : m_results.entrySet())
		{
			try
			{
				Files.createDirectories(entry.getKey().getParent());
				if (Files.exists(entry.getKey()))
				{
					byte[] data = Files.readAllBytes(entry.getKey());
					byte[] toWrite = entry.getValue();
					if (data.length == toWrite.length)
					{

						boolean same = true;
						for (int i=0;i<data.length;i++)
						{
							if (data[i] != toWrite[i])
							{
								same = false;
								break;
							}
						}
						if (same)
						{
							continue;
						}
					}
				}
				Files.write(entry.getKey(),  entry.getValue());
			}
			catch (IOException e)
			{
				e.printStackTrace();
			}
		}
	}
}
